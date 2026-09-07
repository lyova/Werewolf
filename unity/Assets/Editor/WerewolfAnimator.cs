// Authors the werewolf's animator controller from the seller's clips, shaped after the game's own
// animalWolfController / animalDireWolfController (ripped from animals.bundle and read with
// tools\unity-controller-summary.py). For an animal the controller ships INSIDE the prefab; there
// is no vanilla controller to borrow the way the zombie mod borrows ZController.
//
// What AvatarAnimalController (Assembly-CSharp, read with tools\dump-cecil.ps1 -IL) needs from it:
//
//  parameters it writes   Forward Strafe IdleTime (float); Attack MovementState Random BodyPartHit
//                         HitDamage HitDirection (int); IsAlive IsDead IsMoving IsSwim CriticalHit
//                         (bool); AttackTrigger DeathTrigger PainTrigger ElectrocuteTrigger
//                         JumpStart JumpLand BeginCorpseEat EndCorpseEat TriggerAlive (trigger)
//  state TAGS it reads    on layer 0: "Attack"  - IsAnimationAttackPlaying() is
//                         !IsInTransition(0) && tagHash == Attack, after a 0.5 s grace timer.
//                         "Death" - once tagHash == Death && normalizedTime >= 1 && not in
//                         transition, the death animation counts as finished (and the ragdoll
//                         takes over if there is one). "AttackStart"/"AttackReady" only matter
//                         for special actions (Attack >= 3000), which this creature has none of.
//                         on layer 1 (hitLayerIndex = 1 whenever PainResistPerHit >= 0):
//                         "Hit" - IsAnimationHitRunning() is tag == Hit && normalizedTime < 0.55,
//                         and the game drives THAT LAYER'S WEIGHT itself (0.15..0.8 per hit,
//                         fading out), so layer 1 must be the pain layer and nothing else.
//  Forward's unit         Entity.speedForward is a smoothed distance per tick (x2 at steady
//                         state), so Forward ~= speed in m/s divided by 10. Vanilla's wolf blend
//                         thresholds 0 / 0.1 / 0.3 are idle / 1 m/s walk / 3 m/s run.
//
// The clips DO carry root motion now. They arrived animated in place, which forced
// RootMotion="false" on the entity - a setting no other entity in the shipped game uses - and
// that showed in play as a stuttering, half-speed chase. blender-bake-root-motion.py reads the
// travel off the planted feet and writes it onto the root bone, so the blend tree both picks the
// gait and supplies the movement, exactly as every vanilla animal does.

using System;
using System.Globalization;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class WerewolfAnimator
{
    /// <summary>Clips the controller cannot do without; ModelSetup checks they imported.</summary>
    public static readonly string[] RequiredClips =
    {
        "Idle", "Wait", "Walk_4_Legs", "Run_4_Legs", "Attack_L", "Attack_R", "Hit", "Death",
        "Jump", "Fall", "Landing", "Eating", "Eating_Out", "Agr",
    };

    // Where each gait sits on the blend. Vanilla's own numbers, read out of
    // animalWolfController.controller and animalDireWolfController.controller, both of which put
    // idle at 0, the walk at 0.1 and the run at 0.3 on Forward.
    //
    // Under RootMotion="true" these thresholds do not set the speed - the clips do - but they
    // decide WHICH clip plays, so they decide the speed all the same. Forward reaches the entity
    // as EntityAlive.speedForward = moveDirection.z * landMovementFactor * 2.5, and
    // landMovementFactor is 0.1 scaled by speedModifier, which is the MoveSpeed / MoveSpeedAggro
    // out of entityclasses.xml. So Forward = 0.25 * that speed number: 1.2 lands exactly on the
    // run threshold, 0.4 exactly on the walk. See the note in pack/Config/entityclasses.xml.
    const float WalkThreshold = 0.1f;
    const float RunThreshold = 0.3f;

    // A FOURTH node vanilla does not have, and the only honest way to make this creature run
    // faster than its animation does.
    //
    // Under root motion the run clip travels 6.10 m/s, so 7.3 m/s after SizeScale, and that is a
    // hard ceiling: past RunThreshold the blend clamps to its last child and a bigger
    // MoveSpeedAggro changes nothing at all. What DOES change the speed is playing the clip
    // faster - root motion scales exactly with playback rate, and because the legs cycle faster
    // by the same factor the feet keep matching the ground instead of skating.
    //
    // So the run clip goes in twice: once at its authored pace, once at SprintTimeScale. A blend
    // tree plays all its children on one shared normalized time, so two copies of one clip give
    // the same pose either way and only the rate differs - the blend between them is a clean
    // continuous speed dial, no second animation needed.
    //
    //   MoveSpeedAggro 1.2 -> Forward 0.30 ->  7.3 m/s   the animation's own pace
    //   MoveSpeedAggro 1.8 -> Forward 0.45 -> 10.3 m/s   halfway
    //   MoveSpeedAggro 2.4 -> Forward 0.60 -> 13.1 m/s   the ceiling, legs at 1.8x
    //
    // Raise SprintTimeScale for a higher ceiling. Past about 2x the gait starts to read as
    // comically fast rather than fast, which is a judgement to make in game, not here.
    /// <summary>The one animator parameter the game does not write. The mod's DLL sets it to play
    /// the howl; keep the spelling in step with WerewolfHowl.cs.</summary>
    public const string HowlTrigger = "WerewolfHowl";

    /// <summary>Chooses which arm swings: 0 left, 1 right. The mod's DLL sets it; the game has no
    /// parameter of its own that would do. Keep the spelling in step with WerewolfSwipe.cs.</summary>
    public const string SwipeParameter = "WerewolfSwipe";

    const float SprintThreshold = 0.6f;
    const float SprintTimeScale = 1.8f;

    public static AnimatorController Build(string path, Dictionary<string, AnimationClip> clips)
    {
        AnimationClip C(string name)
        {
            if (!clips.TryGetValue(name, out var clip)) throw new Exception("no clip named " + name);
            return clip;
        }

        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null) AssetDatabase.DeleteAsset(path);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(path);

        // Every parameter the game writes, plus vanilla's extras, so nothing it sets is missing.
        // A parameter the game writes that does not exist is harmless (it disables the Animator's
        // warnings), but one that exists and is never read costs nothing either.
        foreach (var p in new[] { "Forward", "Strafe", "IdleTime", "HitRandomValue" })
            controller.AddParameter(p, AnimatorControllerParameterType.Float);
        foreach (var p in new[] { "Attack", "MovementState", "Random", "BodyPartHit", "HitBodyPart", "HitDamage", "HitDirection", "WalkType", "SleeperPose", SwipeParameter })
            controller.AddParameter(p, AnimatorControllerParameterType.Int);
        foreach (var p in new[] { "IsAlive", "IsDead", "IsMoving", "IsSwim", "CriticalHit" })
            controller.AddParameter(p, AnimatorControllerParameterType.Bool);
        foreach (var p in new[] { "AttackTrigger", "DeathTrigger", "PainTrigger", "ElectrocuteTrigger", "JumpStart", "JumpLand", "BeginCorpseEat", "EndCorpseEat", "TriggerAlive", "EndStunTrigger", "SleeperTrigger", HowlTrigger })
            controller.AddParameter(p, AnimatorControllerParameterType.Trigger);
        SetDefaultBool(controller, "IsAlive", true);

        // ------------------------------------------------------------ layer 0: Base Layer
        var sm = controller.layers[0].stateMachine;

        var move = sm.AddState("Move", new Vector3(0, 0, 0));
        move.tag = "";
        // One dimensional on Forward, node for node what the vanilla wolf ships. An earlier
        // version made this two dimensional on Forward and Strafe so side-steps would animate;
        // that has to go now the entity runs on root motion, because under root motion the clip
        // is the movement. All eight compass children were the same forward-running clip, so a
        // large Strafe with Forward near zero would have fired the creature forwards at full run
        // speed while the AI was asking it to step sideways. Vanilla accepts the small ugliness
        // of not animating a side-step rather than that.
        var locomotion = new BlendTree
        {
            name = "Locomotion",
            blendType = BlendTreeType.Simple1D,
            blendParameter = "Forward",
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(locomotion, controller);

        locomotion.AddChild(C("Idle"), 0f);
        locomotion.AddChild(C("Walk_4_Legs"), WalkThreshold);
        locomotion.AddChild(C("Run_4_Legs"), RunThreshold);
        locomotion.AddChild(C("Run_4_Legs"), SprintThreshold);

        // AddChild cannot set the playback rate, and ChildMotion is a struct, so the array has to
        // be read out, changed and put back. Only the last child, the sprint copy, is sped up.
        var gaits = locomotion.children;
        gaits[gaits.Length - 1].timeScale = SprintTimeScale;
        locomotion.children = gaits;

        move.motion = locomotion;
        sm.defaultState = move;

        // Standing still for a while: the seller's 8 s "Wait" idle. IdleTime only ever grows
        // while the creature stands, so this re-enters after every cycle - which is fine, Wait is
        // an idle too. Movement or an attack resets IdleTime and pulls it back to Move.
        var idleBreak = sm.AddState("IdleBreak", new Vector3(-250, 0, 0));
        idleBreak.motion = C("Wait");
        var toBreak = move.AddTransition(idleBreak);
        toBreak.AddCondition(AnimatorConditionMode.IfNot, 0, "IsMoving");
        toBreak.AddCondition(AnimatorConditionMode.Greater, 10f, "IdleTime");
        Fade(toBreak, 0.4f);
        var breakDone = idleBreak.AddTransition(move);
        breakDone.hasExitTime = true;
        breakDone.exitTime = 1f;
        Fade(breakDone, 0.4f, exitTimeKept: true);
        var breakMoving = idleBreak.AddTransition(move);
        breakMoving.AddCondition(AnimatorConditionMode.If, 0, "IsMoving");
        Fade(breakMoving, 0.25f);

        // Attacks. Two swipes; which one plays comes off the Random int the game rolls on every
        // hit and death (0..99), which is the only varying number it hands the animator. Split by
        // speed as vanilla does (Forward 0.15 = moving attack) so both swipes get used either way.
        // Tag "Attack" is what tells the game the attack is playing; the exit at 0.75 of the 1 s
        // clip is when it stops counting, which is when the AI lets the next attack start.
        var attackL = sm.AddState("AttackL", new Vector3(300, -100, 0));
        attackL.motion = C("Attack_L");
        attackL.tag = "Attack";
        var attackR = sm.AddState("AttackR", new Vector3(300, 100, 0));
        attackR.motion = C("Attack_R");
        attackR.tag = "Attack";
        // LEFT OR RIGHT, alternating, not "standing still or moving".
        //
        // Vanilla splits its one attack clip that way - AttackStandingStill below Forward 0.15,
        // Attack above it - because it has a clip for each case. This asset has a left swipe and a
        // right swipe instead, which is a different axis entirely, and splitting them by speed
        // meant only the left one was ever seen: the creature stops to swing, so Forward is
        // essentially always 0 at the moment the trigger fires.
        //
        // Nothing the game writes would serve as the coin. It has a Random parameter but sets it
        // only on a hit taken and on death, never on an attack. So the mod's own DLL sets
        // WerewolfSwipe between swings - see WerewolfSwipe.cs, and the note there about what that
        // means on a dedicated server.
        foreach (var (state, swipe) in new[] { (attackL, 0), (attackR, 1) })
        {
            var t = sm.AddAnyStateTransition(state);
            t.AddCondition(AnimatorConditionMode.If, 0, "AttackTrigger");
            t.AddCondition(AnimatorConditionMode.Equals, swipe, SwipeParameter);
            Fade(t, 0.1f);
            t.canTransitionToSelf = true;
            var back = state.AddTransition(move);
            back.hasExitTime = true;
            back.exitTime = 0.75f;
            Fade(back, 0.25f, exitTimeKept: true);
        }

        // The howl: the asset's "Agr" clip, 2.67 s of the creature rearing up and roaring. The
        // game never fires this - it is the mod's own summon, driven from WerewolfHowl.cs, which
        // sets the trigger straight on the Animator component.
        //
        // It carries no root motion (measured 0.00 m/s on import), and under RootMotion="true"
        // that means the werewolf is rooted to the spot for the whole clip. That is the point:
        // the howl costs it the ground it would have covered chasing you, which is the price of
        // the second werewolf.
        //
        // No tag. The game reads Attack, Death and Hit off the state it is in and would take a
        // howl for a swing; an unknown tag is simply ignored. Entered from anywhere so it can cut
        // in mid-stride, but NOT while dead - IsAlive guards that, because the trigger could
        // otherwise land in the same frame the creature is killed and pull it out of Death.
        var howl = sm.AddState("Howl", new Vector3(300, 250, 0));
        howl.motion = C("Agr");
        howl.tag = "";
        var toHowl = sm.AddAnyStateTransition(howl);
        toHowl.AddCondition(AnimatorConditionMode.If, 0, HowlTrigger);
        toHowl.AddCondition(AnimatorConditionMode.If, 0, "IsAlive");
        Fade(toHowl, 0.15f);
        toHowl.canTransitionToSelf = false;
        var howlDone = howl.AddTransition(move);
        howlDone.hasExitTime = true;
        howlDone.exitTime = 0.9f;
        Fade(howlDone, 0.25f, exitTimeKept: true);

        // Death: entered from anywhere, never left on its own. The clip ends lying down and the
        // state holds its last frame (loop off); the game watches normalizedTime reach 1.
        var death = sm.AddState("Death", new Vector3(600, 0, 0));
        death.motion = C("Death");
        death.tag = "Death";
        var toDeath = sm.AddAnyStateTransition(death);
        toDeath.AddCondition(AnimatorConditionMode.If, 0, "DeathTrigger");
        Fade(toDeath, 0.1f);
        toDeath.canTransitionToSelf = false;
        var revive = death.AddTransition(move);
        revive.AddCondition(AnimatorConditionMode.If, 0, "TriggerAlive");
        Fade(revive, 0.2f);

        // Jumps and falls. The game fires JumpStart when it leaves the ground and JumpLand when it
        // touches down; Fall loops in between, with a time-out back to Move in case a landing is
        // never reported (a jump that ends in water, say).
        var jump = sm.AddState("Jump", new Vector3(0, 250, 0));
        jump.motion = C("Jump");
        var fall = sm.AddState("Fall", new Vector3(250, 250, 0));
        fall.motion = C("Fall");
        var landing = sm.AddState("Landing", new Vector3(500, 250, 0));
        landing.motion = C("Landing");
        var toJump = sm.AddAnyStateTransition(jump);
        toJump.AddCondition(AnimatorConditionMode.If, 0, "JumpStart");
        Fade(toJump, 0.1f);
        toJump.canTransitionToSelf = false;
        var jumpToFall = jump.AddTransition(fall);
        jumpToFall.hasExitTime = true;
        jumpToFall.exitTime = 0.95f;
        Fade(jumpToFall, 0.15f, exitTimeKept: true);
        var fallTimeout = fall.AddTransition(move);
        fallTimeout.hasExitTime = true;
        fallTimeout.exitTime = 2f; // two loops of the 2 s clip
        Fade(fallTimeout, 0.3f, exitTimeKept: true);
        var toLanding = sm.AddAnyStateTransition(landing);
        toLanding.AddCondition(AnimatorConditionMode.If, 0, "JumpLand");
        Fade(toLanding, 0.05f);
        toLanding.canTransitionToSelf = false;
        var landed = landing.AddTransition(move);
        landed.hasExitTime = true;
        landed.exitTime = 0.7f;
        Fade(landed, 0.25f, exitTimeKept: true);

        // Corpse eating (SetNearestCorpseAsTarget in the AI, as the dire wolf and zombie dog do).
        var eat = sm.AddState("Eat", new Vector3(0, -250, 0));
        eat.motion = C("Eating");
        var eatOut = sm.AddState("EatOut", new Vector3(250, -250, 0));
        eatOut.motion = C("Eating_Out");
        var toEat = sm.AddAnyStateTransition(eat);
        toEat.AddCondition(AnimatorConditionMode.If, 0, "BeginCorpseEat");
        Fade(toEat, 0.25f);
        toEat.canTransitionToSelf = false;
        var eatDone = eat.AddTransition(eatOut);
        eatDone.AddCondition(AnimatorConditionMode.If, 0, "EndCorpseEat");
        Fade(eatDone, 0.1f);
        var eatMoving = eat.AddTransition(eatOut);
        eatMoving.AddCondition(AnimatorConditionMode.If, 0, "IsMoving");
        Fade(eatMoving, 0.1f);
        var eatOutDone = eatOut.AddTransition(move);
        eatOutDone.hasExitTime = true;
        eatOutDone.exitTime = 0.9f;
        Fade(eatOutDone, 0.2f, exitTimeKept: true);

        // Swimming: no swim clip in the package, so the four-legged walk at a slower pace keeps
        // the legs moving instead of freezing in the rest pose.
        var swim = sm.AddState("Swim", new Vector3(-250, 250, 0));
        swim.motion = C("Walk_4_Legs");
        swim.speed = 0.6f;
        var toSwim = move.AddTransition(swim);
        toSwim.AddCondition(AnimatorConditionMode.If, 0, "IsSwim");
        Fade(toSwim, 0.25f);
        var fromSwim = swim.AddTransition(move);
        fromSwim.AddCondition(AnimatorConditionMode.IfNot, 0, "IsSwim");
        Fade(fromSwim, 0.25f);

        // ------------------------------------------------------------ layer 1: Pain (additive)
        // Exactly vanilla's shape: an empty default state and one state tagged Hit, on an ADDITIVE
        // layer whose weight the game animates. The Hit clip was imported with an additive
        // reference pose (its own first frame) so it is a delta, not a full pose.
        controller.AddLayer("Pain");
        var layers = controller.layers;
        layers[1].blendingMode = AnimatorLayerBlendingMode.Additive;
        layers[1].defaultWeight = 1f;
        controller.layers = layers;
        var pain = layers[1].stateMachine;
        var empty = pain.AddState("Empty", new Vector3(0, 0, 0));
        empty.motion = null;
        pain.defaultState = empty;
        var hit = pain.AddState("Pain", new Vector3(300, 0, 0));
        hit.motion = C("Hit");
        hit.tag = "Hit";
        var toHit = pain.AddAnyStateTransition(hit);
        toHit.AddCondition(AnimatorConditionMode.If, 0, "PainTrigger");
        Fade(toHit, 0.1f);
        toHit.canTransitionToSelf = true;
        var hitDone = hit.AddTransition(empty);
        hitDone.hasExitTime = true;
        hitDone.exitTime = 1f;
        Fade(hitDone, 0.2f, exitTimeKept: true);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        Debug.Log("[Werewolf] locomotion: idle 0, walk " + WalkThreshold.ToString("0.##", CultureInfo.InvariantCulture)
                  + ", run " + RunThreshold.ToString("0.##", CultureInfo.InvariantCulture)
                  + ", run x" + SprintTimeScale.ToString("0.##", CultureInfo.InvariantCulture)
                  + " at " + SprintThreshold.ToString("0.##", CultureInfo.InvariantCulture)
                  + " on Forward");
        Debug.Log("[Werewolf] controller: " + controller.parameters.Length + " parameters, layer 0 '" + layers[0].name
                  + "' " + sm.states.Length + " states, layer 1 '" + layers[1].name + "' additive "
                  + pain.states.Length + " states; tags Attack/Death on layer 0, Hit on layer 1");
        return controller;
    }

    /// <summary>Fixed-duration crossfade, no exit time unless the caller set one.</summary>
    static void Fade(AnimatorStateTransition t, float seconds, bool exitTimeKept = false)
    {
        if (!exitTimeKept) t.hasExitTime = false;
        t.hasFixedDuration = true;
        t.duration = seconds;
        t.offset = 0f;
        t.interruptionSource = TransitionInterruptionSource.None;
        t.orderedInterruption = true;
    }

    static void SetDefaultBool(AnimatorController controller, string name, bool value)
    {
        var parameters = controller.parameters;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].name == name) parameters[i].defaultBool = value;
        }
        controller.parameters = parameters;
    }
}
