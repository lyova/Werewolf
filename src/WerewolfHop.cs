using HarmonyLib;
using UnityEngine;

/// <summary>
/// Stops the werewolf hopping on the spot instead of walking.
///
/// THE SYMPTOM, and it took all evening to see because every instrument was pointed at the wrong
/// thing. The creature would stand in front of a gap it fits through, aimed correctly, unblocked,
/// with a valid path, and simply never arrive. Seven explanations were tested and killed - capsule
/// width (a zombie bear is wider and walks through), capsule height, JumpMaxDistance, turn rate,
/// run speed, CrouchType, and "it faces the target rather than the waypoint" (it faces the waypoint
/// to within a degree).
///
/// What none of them measured was whether the body was moving at all. Everything in the trace
/// reported what the AI WANTED: Forward 0.30, which the console dutifully converted to "7.3 m/s".
/// Measuring the actual displacement between samples gave 0.0 to 0.4 m/s, and the animator state
/// alongside it gave the reason:
///
///     unknown -> Move -> Jump -> Fall -> Landing -> Move -> unknown -> Move
///
/// It is jumping, over and over. Jump, Fall and Landing are all authored in place - rootMotion 0.00
/// in the import log, like every clip this creature owns except Walk_4_Legs and Run_4_Legs - so the
/// three seconds that cycle takes are three seconds of going nowhere. It walks half a metre between
/// hops, trips the stuck counter again, and hops again. The loop feeds itself.
///
/// WHERE THE HOPS COME FROM. Not JumpMaxDistance - that only decides IsUnreachableSideJump, and
/// removing it changed nothing. They come from the stuck branch of EntityMoveHelper.UpdateMoveHelper:
/// once moveToFailCnt reaches 3, the response is a coin flip between digging, swinging at the
/// obstacle and StartJump. With no BlockedFlags set - the way is open - it lands on the branch that
/// jumps or attacks, and half of those are jumps.
///
/// WHAT THIS DOES. Refuses the jump when there is demonstrably nothing to jump onto: the creature
/// has a path, and the node it is heading for is at its own height. Jumping is for getting UP. On
/// the flat it only costs three seconds of paralysis, and doing it in a loop costs everything.
///
/// A jump towards a node that IS higher still goes ahead untouched, so climbing, ledges and
/// scrambling out of holes all behave exactly as before.
/// </summary>
public static class WerewolfHop
{
    /// <summary>How much higher the next node has to be before a jump is allowed, in metres. Half a
    /// block: anything less is a step, not a climb, and the mover walks up steps by itself.</summary>
    public static float ClimbNeeded = 0.5f;

    public static bool Enabled = true;

    static int refused;

    /// <summary>What the trace prints.</summary>
    public static string Describe(EntityAlive e)
    {
        if (!Enabled) return "hop: allowed (suppression off)";
        if (e == null) return "hop: no entity";

        var rise = NextNodeRise(e);
        return "hop: " + (rise == null
                   ? "no path - jumps allowed"
                   : (rise.Value >= ClimbNeeded
                       ? "next node is " + rise.Value.ToString("0.00") + " m up - jumps allowed"
                       : "next node is level (" + rise.Value.ToString("0.00") + " m) - jumps REFUSED"))
               + ", refused " + refused + " so far";
    }

    /// <summary>How much higher the node it is heading for is, or null when there is no path.
    /// </summary>
    static float? NextNodeRise(EntityAlive e)
    {
        var nav = e.navigator;
        var path = nav == null ? null : nav.currentPath;
        if (path == null) return null;

        var index = path.getCurrentPathIndex();
        if (index < 0 || index >= path.pathLength) return null;

        var point = path.getPathPointFromIndex(index);
        if (point == null) return null;

        return point.y - e.position.y;
    }

    /// <summary>
    /// Werewolf-only on its first line, like every patch in this mod. EntityMoveHelper.StartJump is
    /// the single entry point for an AI jump - EAILeap is the only other caller and this creature
    /// has no Leap task - so refusing here refuses all of them.
    /// </summary>
    [HarmonyPatch(typeof(EntityMoveHelper), "StartJump", new[] { typeof(bool), typeof(float), typeof(float) })]
    public static class NoHoppingOnTheFlat
    {
        static bool Prefix(EntityMoveHelper __instance)
        {
            if (!Enabled) return true;
            var e = __instance.entity;
            if (e == null || !WerewolfWorld.IsWerewolf(e)) return true;

            // No path means no opinion - leave the game to it.
            var rise = NextNodeRise(e);
            if (rise == null || rise.Value >= ClimbNeeded) return true;

            refused++;
            return false;
        }
    }
}
