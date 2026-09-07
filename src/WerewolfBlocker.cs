using HarmonyLib;
using UnityEngine;

/// <summary>
/// Takes the LargeEntityBlocker rigidbody off the werewolf, because that rigidbody - not the
/// character controller - was deciding where the creature is.
///
/// WHAT WAS HAPPENING. Entity.PhysicsInit looks for a child tagged LargeEntityBlocker. When it finds
/// one it tears that node out of the model, parents it to the world, and adds a DYNAMIC Rigidbody
/// to it (no gravity, mass from MassKg, rotation frozen, continuous collision). From then on the
/// entity has two bodies, and the rigidbody is the one that wins:
///
///   Entity.FixedUpdate      lerps the rigidbody 40% of the way toward where the controller went
///   Entity.ApplyFixedUpdate (from Update and from entityCollision) reads the rigidbody back and
///                           calls SetPosition with it - position IS the rigidbody, every frame
///
/// In open ground that is invisible: the rigidbody trails the controller by a few centimetres and
/// catches up. In a 2-block doorway it is everything. The controller's capsule fits and walks
/// through; the rigidbody's three blocker capsules do not, PhysX pushes them back out of the wall,
/// and ApplyFixedUpdate drags the creature back to them. Measured: the controller achieved 7.3 m/s
/// of the 7.35 m/s asked, and 0.7 m/s of it survived to the next tick. The stack trace of the call
/// that took it back reads Entity.ApplyFixedUpdate -> Entity.SetPosition.
///
/// The probe went looking for this in the wrong place for a whole evening because "physicsRB is
/// only set by EntityItem" was taken on faith. It is set for anything with a LargeEntityBlocker,
/// which the prefab builder added because vanilla's bear has one.
///
/// WHAT THIS DOES. Right after PhysicsInit, if the entity is a werewolf and got a blocker rigidbody,
/// the blocker object is destroyed and the fields cleared. Without physicsRB every one of the paths
/// above is skipped and the character controller alone owns the position, exactly as for a wolf.
/// The cost is that a player can walk through a werewolf's body; a wolf allows the same.
///
/// Prefab-side the right fix is to stop building the blocker at all (ModelSetup.BuildBlocker); this
/// patch makes the creature correct with the bundle that is already deployed.
/// </summary>
public static class WerewolfBlocker
{
    public static bool Enabled = true;

    static int removed;

    /// <summary>What the trace prints.</summary>
    public static string Describe(EntityAlive e)
    {
        if (e == null) return "blocker: no entity";
        var rb = e.physicsRB;
        return "blocker: rigidbody " + (rb != null ? "PRESENT (" + (rb.isKinematic ? "kinematic" : "DYNAMIC, mass " + rb.mass.ToString("0")) + ") - ApplyFixedUpdate owns the position" : "none - the character controller owns the position")
               + ", removed from " + removed + " werewolves so far";
    }

    [HarmonyPatch(typeof(Entity), "PhysicsInit")]
    public static class NoSecondBody
    {
        static void Postfix(Entity __instance)
        {
            if (!Enabled || __instance == null || !WerewolfWorld.IsWerewolf(__instance)) return;
            if (__instance.physicsRB == null && __instance.physicsRBT == null) return;

            var rbt = __instance.physicsRBT;
            __instance.physicsRB = null;
            __instance.physicsRBT = null;
            __instance.physicsCapsuleCollider = null;
            if (rbt != null) Object.Destroy(rbt.gameObject);
            removed++;
            Log.Out("[Werewolf] removed the LargeEntityBlocker rigidbody from " + __instance.name + " - the character controller now owns its position");
        }
    }
}
