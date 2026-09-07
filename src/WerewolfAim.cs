using HarmonyLib;
using UnityEngine;

/// <summary>
/// Lets the werewolf aim its claws up or down, the way a zombie can and a wolf cannot.
///
/// THE SYMPTOM. Stand one block above it - on a ramp, a ledge, anything - and it will not land a
/// single hit, while on level ground it connects fine.
///
/// THE CAUSE, read off the IL rather than guessed at. ItemActionAttack.Hit builds its melee probe
/// from EntityAlive.GetLookRay, and there are two versions of that in the game:
///
///   EntityAlive.GetLookRay()   new Ray(position + eyeHeight, GetLookVector())
///   EntityAlive.GetLookVector()  built from Entity.rotation - yaw AND pitch
///
/// which looks like it should aim, until you follow the pitch. EntityLookHelper.onUpdateLook does
/// nothing but DECAY it:
///
///   if (rotation.x >  1) rotation.x -= 1;
///   else if (rotation.x < -1) rotation.x += 1;
///
/// one degree per call, towards zero, and nothing on the animal path ever pushes it the other way.
/// So an animal's attack ray is horizontal by construction. On the flat that is invisible - the
/// trace caught a clean hit with the ray passing 0.01 m from the player's chest. Put the player a
/// block up and the same horizontal line passes a metre low, which no Sphere radius will ever
/// cover.
///
/// A zombie does not have this problem because EntityHuman overrides GetLookVector:
///
///   if (lookAtPosition == Vector3.zero) return base.GetLookVector();
///   return lookAtPosition - getHeadPosition();
///
/// - it aims at the point the AI told it to look at. And EAIApproachAndAttackTarget calls
/// SetLookPosition on every creature it drives, werewolf included, so EntityAlive.SetLookPosition
/// has already stored that point in lookAtPosition for us. The aiming data was there the whole
/// time; the animal branch simply never reads it.
///
/// WHAT THIS DOES. When the creature has an attack target, the melee ray is aimed from the eye at
/// that target's chest instead of straight ahead. Same origin the game already used, same target
/// the game already chose; with no target the game's own level ray is left alone.
///
/// It aims at the TARGET rather than at lookAtPosition, and that distinction was learned the hard
/// way. Copying EntityHuman verbatim - which reads lookAtPosition - had it swinging at the sky:
/// that field is written by whichever task is running, and EAILook, the idle glance-around task,
/// points it anywhere at all. The trace caught the probe 7.42 m from the nearest chest, aimed 7.5 m
/// above the creature's own eye. A zombie survives that code because its look position is almost
/// always its target; ours spends real time idling.
///
/// GetLookVector itself is deliberately left alone. It also feeds IsInViewCone and the sight
/// checks, and widening what the creature can SEE is a different change with different consequences
/// that has not been asked for or tested. Only the attack ray moves.
/// </summary>
public static class WerewolfAim
{
    public static bool Enabled = true;

    /// <summary>What the trace prints, so a miss can be read as a number rather than guessed at.
    /// </summary>
    public static string Describe(EntityAlive e)
    {
        if (e == null) return "aim: no entity";
        var target = e.GetAttackTarget();
        if (target == null) return "aim: no attack target - firing level";
        var origin = e.position + new Vector3(0f, e.GetEyeHeight(), 0f);
        var chest = target.getChestPosition();
        var dir = (chest - origin).normalized;
        var pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
        return "aim: at " + target.EntityName + " chest y " + chest.y.ToString("0.00")
               + " from eye y " + origin.y.ToString("0.00")
               + ", pitch " + pitch.ToString("0.0") + " deg"
               + (Enabled ? "" : "  (AIMING OFF - firing level)");
    }

    /// <summary>
    /// Aims the melee probe. Werewolf-only on the first line, like every other patch in this mod.
    ///
    /// EntityHuman overrides GetLookRay outright, so patching the EntityAlive one cannot touch a
    /// player, a zombie or a bandit - they never call it. Other animals do call it, and they leave
    /// on that same first line.
    /// </summary>
    [HarmonyPatch(typeof(EntityAlive), "GetLookRay")]
    public static class AimTheClaw
    {
        static bool Prefix(EntityAlive __instance, ref Ray __result)
        {
            if (!Enabled || __instance == null || !WerewolfWorld.IsWerewolf(__instance)) return true;

            // Aim at the ATTACK TARGET, not at lookAtPosition.
            //
            // The first version of this used lookAtPosition, copying EntityHuman verbatim, and the
            // trace showed why that is wrong here: lookAtPosition is set by whichever task happens
            // to be running, and EAILook - the idle "glance around" task - points it anywhere. It
            // was caught aiming 7.5 m above its own eye, with the probe passing 7.42 m from the
            // nearest chest. A zombie gets away with the same code because its look position is
            // nearly always its target; ours spends real time in EAILook and EAIApproachSpot.
            //
            // With no attack target there is nothing to aim at, so the game's own level ray stands.
            var target = __instance.GetAttackTarget();
            if (target == null) return true;

            var origin = __instance.position + new Vector3(0f, __instance.GetEyeHeight(), 0f);
            var direction = target.getChestPosition() - origin;
            if (direction.sqrMagnitude < 0.0001f) return true;

            __result = new Ray(origin, direction);
            return false;
        }
    }
}
