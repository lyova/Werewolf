using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Makes a werewolf finish a fight with the zombies around it before going back to the player.
///
/// WHAT THE GAME ALREADY DOES. EAISetAsTargetIfHurt switches a creature onto whatever just hurt it,
/// and the werewolf's AITarget list now names EntityZombie among the classes it will retaliate
/// against. That much is vanilla and needs no code.
///
/// WHAT IT DOES NOT DO is stay. The moment that one zombie dies, targeting falls back to
/// EAISetNearestEntityAsTarget, which prefers the player - so a werewolf swarmed by three zombies
/// kills one and immediately turns its back on the other two to chase a player standing on a roof
/// it cannot reach anyway. That is the case this fixes.
///
/// HOW. A werewolf enters a brawl the moment its own attack target is a zombie - that is, only
/// after the game itself decided it had been hurt by one. While it is brawling and any zombie
/// remains within BrawlRadius, its target is kept on the nearest of them; when the last one in that
/// radius is dead or gone, the brawl ends and vanilla targeting resumes untouched.
///
/// It never STARTS a fight. Walking past a zombie does nothing at all - proximity alone is not
/// enough, and that is deliberate: keying off distance would have the creature abandon a chase
/// every time its route happened to pass a horde.
///
/// The radius is bounded on purpose. It is what "the ones on me right now" means, and widening it
/// without limit would have the werewolf wander off to pick fights it was never in - which is the
/// opposite failure to the one this fixes.
/// </summary>
public static class WerewolfBrawl
{
    /// <summary>
    /// How close a zombie has to be to count as part of the brawl, in blocks.
    ///
    /// 5, raised from 3. Three covered only what could physically reach it - vanilla zombie reach
    /// is about 1.6 m - and in play that ended the brawl too early: the werewolf killed the two on
    /// top of it and turned its back on the three a step behind them, which still reads as losing
    /// interest mid-fight. Five is about the distance a zombie closes in the time one swing takes,
    /// so anything already coming for it counts as part of the same fight.
    ///
    /// This only decides how long a brawl LASTS. Nothing here starts one: that still needs the game
    /// to have handed the werewolf a zombie as its target, which only happens when a zombie hurt it.
    /// </summary>
    public static float BrawlRadius = 5f;

    /// <summary>Ticks of target life handed to each re-target. Short deliberately: the brawl is
    /// re-evaluated four times a second, so this only has to survive until the next look.</summary>
    const int HoldTicks = 60;

    public static bool Enabled = true;

    static readonly Dictionary<int, int> brawling = new Dictionary<int, int>();
    static readonly List<int> gone = new List<int>();
    static float lastTick;

    /// <summary>True while this creature is working through the zombies on top of it.</summary>
    public static bool IsBrawling(EntityAlive e)
    {
        return Enabled && e != null && brawling.ContainsKey(e.entityId);
    }

    public static void Tick()
    {
        if (!Enabled) return;

        var now = Time.time;
        if (now - lastTick < 0.25f) return;
        lastTick = now;

        var world = WerewolfWorld.World;
        if (world == null) return;

        var live = WerewolfWorld.Live();
        foreach (var e in live)
        {
            if (e == null || e.IsDead()) continue;

            var target = e.GetAttackTarget();
            var fighting = target as EntityZombie;

            // Entering a brawl is the game's decision, not ours: it only counts once the creature
            // has ALREADY been turned onto a zombie by EAISetAsTargetIfHurt.
            if (fighting != null && !fighting.IsDead()) brawling[e.entityId] = fighting.entityId;
            if (!brawling.ContainsKey(e.entityId)) continue;

            // Still busy with one that is alive and close? Leave it alone.
            if (fighting != null && !fighting.IsDead()
                && (fighting.position - e.position).magnitude <= BrawlRadius) continue;

            var next = NearestZombie(e);
            if (next == null)
            {
                // Nothing left on it. Hand targeting back to the game with no interference - the
                // current target is left exactly as vanilla set it.
                brawling.Remove(e.entityId);
                continue;
            }

            e.SetAttackTarget(next, HoldTicks);
            brawling[e.entityId] = next.entityId;
        }

        Forget(live);
    }

    static EntityZombie NearestZombie(EntityAlive e)
    {
        var world = WerewolfWorld.World;
        if (world == null) return null;

        EntityZombie best = null;
        var bestSq = BrawlRadius * BrawlRadius;

        var near = world.GetEntitiesInBounds(typeof(EntityZombie),
            new Bounds(e.position, Vector3.one * (BrawlRadius * 2f)), new List<Entity>());
        if (near == null) return null;

        foreach (var other in near)
        {
            var zombie = other as EntityZombie;
            if (zombie == null || zombie.IsDead()) continue;
            var distanceSq = (zombie.position - e.position).sqrMagnitude;
            if (distanceSq > bestSq) continue;
            bestSq = distanceSq;
            best = zombie;
        }
        return best;
    }

    static void Forget(List<EntityAlive> live)
    {
        if (brawling.Count <= live.Count) return;
        gone.Clear();
        foreach (var id in brawling.Keys)
        {
            var alive = false;
            foreach (var e in live)
            {
                if (e.entityId != id) continue;
                alive = true;
                break;
            }
            if (!alive) gone.Add(id);
        }
        foreach (var id in gone) brawling.Remove(id);
    }

    /// <summary>What the trace prints.</summary>
    public static string Describe(EntityAlive e)
    {
        if (!Enabled) return "brawl: off";
        if (e == null) return "brawl: no entity";
        var near = NearestZombie(e);
        var inBrawl = brawling.ContainsKey(e.entityId);
        return "brawl: " + (inBrawl ? "ON" : "off")
               + ", nearest zombie within " + BrawlRadius.ToString("0.#") + " m: "
               + (near != null ? near.EntityName + " at " + (near.position - e.position).magnitude.ToString("0.0") + " m"
                               : "none");
    }
}
