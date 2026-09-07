using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// The howl: the werewolf's second attack, which calls another one of its kind instead of hitting.
///
/// The rules, as asked for:
///   * a roll every 10 seconds, per werewolf, and only while it has something to fight
///   * 10% of those rolls summon
///   * TWO summons in total, counted across the whole pack - it makes no difference whether the
///     original called them or a called one did
///   * on the second, the whole thing locks for 10 minutes, then resets and can go again
///
/// So the worst case is three werewolves on you at once, and then a ten-minute quiet spell. The
/// example from the brief - the first calls a second, the second calls a third, locked - is
/// exactly what the shared counter gives.
///
/// The times are real seconds, not game hours, because "every 10 seconds" during a fight can only
/// sensibly mean the clock you fight on.
///
/// The animation is the asset's "Agr" clip, wired into the controller as the Howl state. It has no
/// root motion, so the werewolf is nailed in place for its 2.67 seconds - the howl costs it the
/// ground it would otherwise have closed. That is the balance of it and it is deliberate.
/// </summary>
public static class WerewolfHowl
{
    // ---------------------------------------------------------------- the dials

    /// <summary>Seconds between rolls. Each live werewolf rolls once per pass.</summary>
    public static float RollSeconds = 10f;

    /// <summary>Chance a roll summons, 0..1.</summary>
    public static float Chance = 0.10f;

    /// <summary>Summons allowed before the lock. Shared by every werewolf alive.</summary>
    public static int PackLimit = 2;

    /// <summary>Real minutes the howl is locked out once the limit is reached.</summary>
    public static float LockMinutes = 10f;

    /// <summary>
    /// How far from the howler the new one lands, in blocks.
    ///
    /// 50, raised from 25 and originally 10. The answer to a howl should come from somewhere out in
    /// the dark and close in, not appear at arm's length; at 7 m/s (9.8 at night) it still covers
    /// 50 m in seven seconds, so the summon joins the fight while the fight is still on.
    ///
    /// This is about as far as it can sensibly go. The spot has to be inside loaded terrain -
    /// WerewolfWorld.GroundNear reads World.GetHeightAt for it, and beyond the player's loaded
    /// chunks that returns nothing useful - and a creature spawned outside a chunk observer's range
    /// is unloaded again by the game. The single-player load radius is comfortably over 50 m, so
    /// this is safe; going much further would start dropping summons into unloaded ground.
    ///
    /// Raising it past 10 needed a matching change in WerewolfWorld.GroundNear, which used to
    /// refuse a terrain height more than 3 m from the summoner's - nonsense over this distance.
    /// </summary>
    public static float SummonDistance = 50f;

    /// <summary>Off switch for testing; 'werewolf howl off'.</summary>
    public static bool Enabled = true;

    /// <summary>
    /// How long a howl holds the creature, in seconds.
    ///
    /// 3.5, matched to the SOUND rather than the animation. The Agr clip is 2.67 s (the controller
    /// leaves it at 0.9, so ~2.4 s on screen) but the howl recordings run 3.46-4.22 s, and a swing
    /// landing while the roar is still in the air read as two things happening at once. The owner
    /// chose to hold the creature for the sound instead of trimming the clips: after the animation
    /// ends it stands for the last second in its idle pose (no root motion, so it does not drift)
    /// until the howl has finished, then fights on.
    /// </summary>
    public static float HowlSeconds = 3.5f;

    // ---------------------------------------------------------------- the state

    static readonly System.Random Random = new System.Random();
    static float nextRoll;
    static float lockedUntil;
    static int summoned;
    static bool forceNext;

    /// <summary>Who is mid-howl, and until when (Time.time).</summary>
    static readonly Dictionary<int, float> howlingUntil = new Dictionary<int, float>();

    public static bool IsHowling(Entity e)
    {
        float until;
        return e != null && howlingUntil.TryGetValue(e.entityId, out until) && Time.time < until;
    }

    public static bool Locked => Time.time < lockedUntil;
    public static int Summoned => summoned;
    public static float LockSecondsLeft => Mathf.Max(0f, lockedUntil - Time.time);

    /// <summary>Wipes the counter and any lock. 'werewolf howl reset'.</summary>
    public static void Reset()
    {
        summoned = 0;
        lockedUntil = 0f;
        nextRoll = 0f;
    }

    /// <summary>Makes the next roll a certainty, for 'werewolf howl now'. It skips the dice and
    /// the wait, and NOTHING else - the pack limit and the lock still apply, because those are
    /// the parts worth watching and a test that switched them off would only prove the spawn
    /// code runs.</summary>
    public static void ForceNextRoll()
    {
        forceNext = true;
        nextRoll = 0f;
    }

    // ---------------------------------------------------------------- the tick

    public static void Tick()
    {
        if (!Enabled) return;
        if (Time.time < nextRoll) return;
        nextRoll = Time.time + RollSeconds;

        if (Locked) return;

        // The lock has just run out: start the pack's allowance again.
        if (summoned >= PackLimit)
        {
            summoned = 0;
            Log.Out("[Werewolf] howl unlocked, the pack can call " + PackLimit + " more");
        }

        var world = WerewolfWorld.World;
        if (world == null) return;

        foreach (var e in WerewolfWorld.Live())
        {
            if (summoned >= PackLimit) break;

            // Only in a fight. It is a second ATTACK, not idle flavour, and without this the
            // wasteland would quietly fill with werewolves nobody ever saw arrive.
            if (e.GetAttackTarget() == null) continue;

            if (!forceNext && Random.NextDouble() >= Chance) continue;
            forceNext = false;

            Howl(e);
            if (!Summon(e)) continue;

            summoned++;
            if (summoned >= PackLimit)
            {
                lockedUntil = Time.time + LockMinutes * 60f;
                Log.Out("[Werewolf] pack limit of " + PackLimit + " reached, howl locked for "
                        + LockMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min");
            }
        }
    }

    // ---------------------------------------------------------------- the parts

    static void Howl(EntityAlive e)
    {
        // The sound first, because it is the half that reaches everybody. PlayOneShot goes through
        // the game's own audio and network path, so on a dedicated server every client hears the
        // howl even though the animation below is local - see WerewolfWorld.ModelAnimator.
        //
        // werewolf_howl is defined in this mod's sounds.xml. No entity property points at it: the
        // game has no concept of a summon, so nothing would ever play it on our behalf.
        e.PlayOneShot(HowlSoundName, false, false, false, null, 1f);

        // Hold the swings for the length of the clip - see NoSwingWhileHowling below for why the
        // animation was never seen in a fight without this.
        howlingUntil[e.entityId] = Time.time + HowlSeconds;

        var animator = WerewolfWorld.ModelAnimator(e);
        if (animator == null) return;
        // Matches the parameter WerewolfAnimator.cs adds to the controller. If a future rebuild
        // drops it, SetTrigger on a name the controller does not have is a no-op plus a warning,
        // never an exception - so a stale bundle costs the animation and nothing else.
        animator.SetTrigger(HowlTriggerName);
    }

    /// <summary>
    /// No swings while howling.
    ///
    /// Without this the howl was a sound and nothing else whenever it landed in a fight - which is
    /// the only time it ever fires. The Howl state carries no tag, so the game's
    /// IsAnimationAttackPlaying() is false during it and EAIApproachAndAttackTarget swings again on
    /// its very next turn; Attack() fires AttackTrigger, and the controller's Any State -> AttackL/R
    /// transition cuts the Howl state off 0.15 s after it started. The player heard a howl and saw
    /// a swipe.
    ///
    /// Refusing Attack() for the length of the clip is the whole fix: no trigger, nothing to
    /// interrupt the state, and the AI simply asks again next tick. The clip has no root motion,
    /// so under RootMotion="true" the creature also stands still for it - the howl now costs both
    /// the ground and the swing, which is what a summon should cost.
    ///
    /// Werewolf-only on its first line, like every patch in this mod.
    /// </summary>
    [HarmonyPatch(typeof(EntityAlive), "Attack", new[] { typeof(bool) })]
    public static class NoSwingWhileHowling
    {
        static bool Prefix(EntityAlive __instance, ref bool __result)
        {
            if (!WerewolfWorld.IsWerewolf(__instance) || !IsHowling(__instance)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>Kept in step with WerewolfAnimator.HowlTrigger, which lives in the Unity project
    /// and cannot be referenced from here.</summary>
    public const string HowlTriggerName = "WerewolfHowl";

    /// <summary>The SoundDataNode in this mod's sounds.xml.</summary>
    public const string HowlSoundName = "werewolf_howl";

    static bool Summon(EntityAlive howler)
    {
        var world = WerewolfWorld.World;
        if (world == null) return false;

        // Its OWN class, so a snow werewolf calls a snow werewolf and the desert one stays brown.
        // No lookup needed at all - the howler is already the answer.
        var classId = howler.entityClass;

        // Keep it off the screen of whoever is being hunted. If the target is not a player -
        // the werewolf is fighting a zombie - the nearest player is the one to hide from, because
        // they are the one who would see it pop in.
        // Who the new one goes after. The howler's own target first, but if that is not a player -
        // or it has none at all, which happens when the howl lands between chases - fall back to
        // the nearest player. A summon that arrives without a target stands about working out what
        // happened, which reads as the skill having done nothing.
        var target = howler.GetAttackTarget();
        var player = WerewolfWorld.NearestPlayer(howler.position);
        if (!(target is EntityPlayer) && player != null) target = player;
        var hideFrom = player;

        var where = WerewolfWorld.GroundNear(howler.position, SummonDistance, Random, hideFrom);
        var facing = new Vector3(0f, (float)(Random.NextDouble() * 360.0), 0f);

        var spawned = EntityFactory.CreateEntity(classId, where, facing) as EntityAlive;
        if (spawned == null)
        {
            Log.Warning("[Werewolf] howl: could not create entity class " + classId);
            return false;
        }

        // Unknown - the same source a console 'se' spawn gets - and neither Biome nor Dynamic,
        // for two different reasons:
        //
        //   Biome    would be charged against the biome spawner's budget and culled by the gate's
        //            ambient cap, so calling for help would quietly stop the world spawning more.
        //   Dynamic  is what horde and screamer zombies use, and EntityEnemy.IsSavedToFile returns
        //            FALSE for a live Dynamic entity. The summons were not written to the chunk, so
        //            a save-and-reload mid-fight brought back the original alone and the two it
        //            called simply ceased to exist. Reported as "reload, only one left" and it
        //            looked exactly as cheap as it sounds.
        //
        // Unknown is saved like anything else, is exempt from the gate (WerewolfSpawnGate only
        // governs Biome), is not counted by any spawner, and despawns on the ordinary
        // "nobody has seen it for a long while" rule rather than Dynamic's "no player anywhere".
        spawned.SetSpawnerSource(EnumSpawnerSource.Unknown);
        world.SpawnEntityInWorld(spawned);

        // Straight into the same fight, so it charges instead of standing around working out
        // what just happened.
        if (target != null) spawned.SetAttackTarget(target, 2000);

        var fromHowler = (where - howler.position).magnitude;
        var fromTarget = target != null ? (where - target.position).magnitude : -1f;
        Log.Out("[Werewolf] " + howler.entityId + " howled up " + spawned.entityId
                + " at " + where.ToCultureInvariantString("0.0")
                + "  " + fromHowler.ToString("0.0") + " m from the howler"
                + (fromTarget >= 0f ? ", " + fromTarget.ToString("0.0") + " m from " + target.EntityName : "")
                + ", target " + (target != null ? target.EntityName : "none")
                + "  (" + (summoned + 1) + " of " + PackLimit + ")");
        return true;
    }
}
