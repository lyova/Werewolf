using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Keeps a werewolf's contact with a wall from being forgotten between frames, so the game's
/// own EAIBreakBlock can wake up and break it.
///
/// THE PROBLEM. EAIBreakBlock.CanExecute has exactly one condition this creature ever fails:
/// BlockedTime >= 0.35. Everything else was confirmed passing in the trace - CanBreakBlocks set,
/// not jumping, BlockedFlags matching the mask, and a solid block identified in front of it.
///
/// BlockedTime is accumulated in EntityMoveHelper.UpdateMoveHelper, 0.05 at a time, and ONLY while
/// BlockedFlagsAfterCrouch is non-zero; the moment it is zero the counter is slammed back to 0. So
/// the requirement is not "a third of a second of being blocked", it is a third of a second of
/// UNBROKEN contact - seven consecutive frames. The flags themselves are wiped and recomputed on a
/// four-tick cycle, and anything that interrupts contact for a single cycle starts the count over.
///
/// A werewolf sprinting into a wall at nine metres a second does not hold still contact; it hits,
/// rebounds, re-paths and hits again. A shambling zombie does, which is why the vanilla number
/// works for zombies and not here.
///
/// WHAT THIS DOES. It tracks contact itself, with a short grace, and writes the tracked figure back
/// into BlockedTime - but only on a tick where the creature is in contact right now. Contact that
/// flickers off for a fraction of a second no longer erases what came before it; contact that stops
/// for good decays away. The number handed back is never larger than the contact actually observed,
/// so this cannot make a creature break something it never touched, and a creature merely running
/// past a wall gains nothing from it.
///
/// This is the SECOND of the two doors into breaking, and the narrower one. The first - the dig,
/// the swing at the obstacle, the jump, all three inside UpdateMoveHelper - sits behind
/// moveToFailCnt reaching 3, which counts ticks where the creature failed to get 2 cm closer to its
/// current waypoint and is reset by 7 cm of progress or by any call to SetMoveTo. A shambling
/// zombie pressed against a wall satisfies that instantly; a creature running at 7 m/s with a
/// re-planned path never does. That door is left alone here. EAIBreakBlock is the other one, and it
/// asks for something this creature genuinely does achieve - it just achieves it in pieces.
///
/// (The per-frame instrumentation that lived here while the doorway bug was hunted is gone; the
/// bug turned out to be the LargeEntityBlocker rigidbody, see WerewolfBlocker.)
/// </summary>
public static class WerewolfPress
{
    /// <summary>
    /// Seconds of lost contact tolerated before the accumulated press decays.
    ///
    /// 1.5, raised from 0.5 after measuring. Contact against a wall runs at about 15-20 per cent of
    /// ticks - real, repeated, and spread out - so with half a second of grace and a decay twice as
    /// fast as the gain, the count drained as fast as it built and parked at 0.15. A creature that
    /// spends a fifth of every second shouldering a wall over many seconds is pressed against it by
    /// any sensible reading, and this lets that accumulate.
    /// </summary>
    const float GraceSeconds = 1.5f;

    /// <summary>How quickly the accumulated press drains once contact is genuinely gone, in seconds
    /// of press per second of no contact. Slower than the gain, deliberately: leaving the wall for
    /// good should lose the progress, but not faster than shouldering it can win it back.</summary>
    const float DecayRate = 0.75f;

    public static bool Enabled = true;

    class Press
    {
        public float pressedFor;    // contact we are prepared to vouch for, in seconds
        public float lostFor;       // how long contact has been missing
        public float best;          // longest unbroken contact seen, for the trace
        public float raw;           // the game's own BlockedTime, for comparison
        public int breaks;          // times contact was lost at all
    }

    static readonly Dictionary<int, Press> pressing = new Dictionary<int, Press>();

    /// <summary>What the trace prints. Says both what the game measured and what was held on to,
    /// so the two can be compared rather than assumed equal.</summary>
    public static string Describe(EntityAlive e)
    {
        Press p;
        if (e == null || !pressing.TryGetValue(e.entityId, out p)) return "press: never in contact with anything";
        return "press: holding " + p.pressedFor.ToString("0.00") + " s (needs 0.35), game's own "
               + p.raw.ToString("0.00") + ", longest unbroken " + p.best.ToString("0.00")
               + " s, contact lost " + p.breaks + " times"
               ;
    }

    /// <summary>
    /// Blocked by the same rule EAIBreakBlock.CanExecute uses, mask and all.
    ///
    ///   crouchType != 0        -> 5
    ///   physicsHeight >= 1     -> 7
    ///   otherwise              -> 5
    /// </summary>
    static bool Contact(EntityMoveHelper mh, EntityAlive e)
    {
        var mask = e.crouchType != 0 ? 5 : (e.physicsHeight >= 1f ? 7 : 5);
        return (mh.BlockedFlags & mask) > 0;
    }

    public static void Forget(int entityId)
    {
        pressing.Remove(entityId);
    }

    [HarmonyPatch(typeof(EntityMoveHelper), "UpdateMoveHelper")]
    public static class HoldTheContact
    {
        static void Postfix(EntityMoveHelper __instance)
        {
            if (!Enabled) return;
            var e = __instance.entity;
            if (e == null || !WerewolfWorld.IsWerewolf(e)) return;

            Press p;
            if (!pressing.TryGetValue(e.entityId, out p))
            {
                p = new Press();
                pressing[e.entityId] = p;
            }

            p.raw = __instance.BlockedTime;

            // Contact is counted by the SAME test EAIBreakBlock applies, not the one
            // UpdateMoveHelper uses to fill BlockedTime. Those are two different fields, and the
            // difference is the whole bug:
            //
            //   UpdateMoveHelper  adds to BlockedTime while BlockedFlagsAfterCrouch > 0
            //   EAIBreakBlock     requires (BlockedFlags & mask) > 0, and BlockedTime >= 0.35
            //
            // On this creature BlockedFlagsAfterCrouch is never set at all - measured over hundreds
            // of ticks, 0% - while BlockedFlags reads 1 in about a fifth of them and the trace says
            // "flags 1 & 7 ok". So the task that breaks blocks already considers the creature
            // blocked, and the clock it waits on is fed from a field that never moves. Counting the
            // first version of this by afterCrouch meant it accumulated nothing, which is exactly
            // what the measurement showed.
            if (Contact(__instance, e))
            {
                if (p.lostFor > 0f) p.breaks++;
                p.lostFor = 0f;
                p.pressedFor += Time.deltaTime;
                if (p.pressedFor > p.best) p.best = p.pressedFor;
            }
            else
            {
                p.lostFor += Time.deltaTime;
                if (p.lostFor > GraceSeconds)
                {
                    p.pressedFor -= Time.deltaTime * DecayRate;
                    if (p.pressedFor < 0f) p.pressedFor = 0f;
                }
            }

            // Only ever raise BlockedTime while the creature is IN CONTACT this very tick. That one
            // condition is what keeps this honest:
            //
            //   * a creature brushing a wall as it runs past gets nothing, because by the next tick
            //     it is not in contact and the game's own figure stands;
            //   * a creature leaning on a wall in fits and starts keeps what it has earned, because
            //     the gaps no longer erase it.
            //
            // It can never claim more contact than was actually observed - pressedFor is only ever
            // advanced by real frames of contact above, and decays once contact genuinely stops.
            if (!Contact(__instance, e)) return;
            if (p.pressedFor > __instance.BlockedTime) __instance.BlockedTime = p.pressedFor;
        }
    }
}
