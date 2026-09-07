using UnityEngine;

/// <summary>
/// Lets the werewolf come after a player it can hear but cannot see.
///
/// WHY IT NEEDS THIS. Vanilla hearing does not produce a target. EAISetNearestEntityAsTarget.SeekNoise
/// takes the noise, walks it back along the player's own breadcrumb trail and calls
/// SetInvestigatePosition - a place to go and sniff, nothing more. A target only ever comes from
/// SEEING you (FindTargetPlayer) or from being HURT by you (the revenge path). Stand on a roof
/// behind a wall and fire near the creature and it has neither: the trace read "target none" on
/// every werewolf present, with ai: EAIApproachSpot.
///
/// And with no target, nothing that breaks walls can run:
///
///   EAIDestroyArea       CanExecute returns false on its second line without GetAttackTarget()
///   the UpdateMoveHelper branch   needs moveToFailCnt >= 3, which a creature running at 7 m/s
///                                 resets constantly - measured, it never got past 2
///   EAIBreakBlock        needs no target, but does need sustained contact with the wall, and
///                                 nothing was driving the creature into it
///
/// A zombie wins this situation by being slow, not by being clever: it shuffles to the noise, plants
/// itself against the wall and both of those doors open on their own.
///
/// WHAT THIS DOES. When the game has already decided the creature is investigating a noise -
/// investigatePositionTicks is counting, which only SetInvestigatePosition starts - and a player is
/// within Range, that player becomes the attack target. It does not invent an alert, it converts one
/// the game raised.
///
/// It is deliberately bounded in three ways:
///
///   * it never overrides an existing target, so it cannot pull the creature out of a fight;
///   * the target is granted for HoldTicks, the same 600 - thirty seconds - vanilla hands out when
///     it can see you, after which it expires normally;
///   * it needs the player inside Range, which is smaller than the distance the creature can hear.
///
/// The consequence is deliberate and worth stating plainly: a werewolf that hears you knows roughly
/// where you are and commits to it, so a wall alone stops being a safe place to shoot from. That is
/// the whole point of the change.
/// </summary>
public static class WerewolfEars
{
    /// <summary>How close the player has to be for a heard noise to become a hunt, in blocks. The
    /// creature can HEAR at 20 (the first number of the EntityPlayer pair in AITarget), so this is
    /// the tighter of the two and the one that decides.</summary>
    public static float Range = 20f;

    /// <summary>Ticks of target life granted. 600 is what EAISetNearestEntityAsTarget itself gives
    /// on a turn where it can see you - thirty seconds - so this is no more generous than sight.
    /// </summary>
    const int HoldTicks = 600;

    public static bool Enabled = true;

    static float lastTick;

    public static void Tick()
    {
        if (!Enabled) return;

        var now = Time.time;
        if (now - lastTick < 0.25f) return;
        lastTick = now;

        foreach (var e in WerewolfWorld.Live())
        {
            if (e == null || e.IsDead()) continue;

            // Never take a fight away from it.
            if (e.GetAttackTarget() != null) continue;

            // The game's own "I heard something" state. investigatePositionTicks is only ever
            // started by SetInvestigatePosition, which on this creature means SeekNoise.
            if (e.investigatePositionTicks <= 0) continue;

            var player = WerewolfWorld.NearestPlayer(e.position);
            if (player == null || player.IsDead()) continue;
            if ((player.position - e.position).magnitude > Range) continue;

            e.SetAttackTarget(player, HoldTicks);
        }
    }

    /// <summary>What the trace and the console print.</summary>
    public static string Describe(EntityAlive e)
    {
        if (!Enabled) return "ears: off";
        if (e == null) return "ears: no entity";

        var player = WerewolfWorld.NearestPlayer(e.position);
        var distance = player != null ? (player.position - e.position).magnitude : -1f;
        return "ears: investigating " + (e.investigatePositionTicks > 0 ? "YES" : "no")
               + " (" + e.investigatePositionTicks + " ticks left)"
               + ", nearest player " + (distance >= 0f ? distance.ToString("0.0") + " m" : "none")
               + " vs range " + Range.ToString("0.#")
               + ", target " + (e.GetAttackTarget() != null ? e.GetAttackTarget().EntityName : "none");
    }
}
