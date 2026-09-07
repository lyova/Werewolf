using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The two questions every other file in this assembly asks: which werewolves are alive right now,
/// and is this machine the one allowed to decide anything about them.
///
/// On the authority question: entity spawning, despawning and AI are the SERVER's business. A
/// client holds copies with <c>isEntityRemote</c> set, and if it acted on them too, a summoned
/// werewolf would be spawned twice and a gated one removed underneath the server. Every loop here
/// skips remote entities, which makes the callers server-only without them having to think about
/// it - in single player the host is the server, so it all still runs.
/// </summary>
public static class WerewolfWorld
{
    /// <summary>Every entity_class in this mod starts with this, so the diagnostic T1/T2/T3 copies
    /// and any future biome variant are covered without listing them.</summary>
    public const string ClassPrefix = "animalWerewolf";

    /// <summary>The one that spawns in the world and that the howl summons.</summary>
    public const string MainClass = "animalWerewolf";

    public static World World => GameManager.Instance != null ? GameManager.Instance.World : null;

    /// <summary>Alive werewolves this machine is responsible for. Never null.</summary>
    public static List<EntityAlive> Live(bool localOnly = true)
    {
        var found = new List<EntityAlive>();
        var world = World;
        if (world == null || world.EntityAlives == null) return found;

        foreach (var e in world.EntityAlives)
        {
            if (e == null || e.IsDead()) continue;
            if (localOnly && e.isEntityRemote) continue;
            if (!IsWerewolf(e)) continue;
            found.Add(e);
        }
        return found;
    }

    public static bool IsWerewolf(Entity e)
    {
        if (e == null || !EntityClass.list.ContainsKey(e.entityClass)) return false;
        var name = EntityClass.list[e.entityClass].entityClassName;
        return !string.IsNullOrEmpty(name) && name.StartsWith(ClassPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The entity class id for a class name.
    ///
    /// RETURNS A BOOL RATHER THAN A SENTINEL, and that is the whole point of it. An entity class id
    /// IS the class name's GetHashCode - EntityClass.Add keys the table with it and
    /// EntityClass.FromString is that call and nothing else - so roughly half of all valid ids are
    /// NEGATIVE. An earlier version returned -1 for "not found", and the howl spent two play tests
    /// summoning nothing and logging "no entity class named animalWerewolf" while the class sat
    /// right there in the table: its id was simply negative and the caller read that as failure.
    /// </summary>
    public static bool TryClassId(string className, out int id)
    {
        id = EntityClass.FromString(className);
        if (EntityClass.list.ContainsKey(id)) return true;

        foreach (var pair in EntityClass.list.Dict)
        {
            if (pair.Value != null &&
                string.Equals(pair.Value.entityClassName, className, StringComparison.OrdinalIgnoreCase))
            {
                id = pair.Key;
                return true;
            }
        }

        var seen = new List<string>();
        foreach (var pair in EntityClass.list.Dict)
        {
            if (pair.Value != null && pair.Value.entityClassName != null &&
                pair.Value.entityClassName.IndexOf("erewolf", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                seen.Add(pair.Value.entityClassName);
            }
        }
        Log.Warning("[Werewolf] no entity class '" + className + "' (hash " + id + "); the table holds "
                    + (seen.Count == 0 ? "no werewolf classes at all" : string.Join(", ", seen.ToArray()))
                    + " out of " + EntityClass.list.Count + " classes");
        return false;
    }

    /// <summary>The living player closest to a point, or null if there are none.</summary>
    public static EntityPlayer NearestPlayer(Vector3 point)
    {
        var world = World;
        if (world == null || world.Players == null || world.Players.list == null) return null;

        EntityPlayer best = null;
        var bestSq = float.MaxValue;
        foreach (var p in world.Players.list)
        {
            if (p == null || p.IsDead()) continue;
            var d = (p.position - point).sqrMagnitude;
            if (d >= bestSq) continue;
            bestSq = d;
            best = p;
        }
        return best;
    }

    /// <summary>
    /// The Animator on the model, which is where the mod's own animation triggers have to go. The
    /// game's AvatarController only ever writes the parameters it knows about, and the howl is not
    /// one of them.
    ///
    /// Note this is local: a trigger set here plays the clip on THIS machine. The summon it
    /// accompanies is replicated by the game because spawning is, but on a dedicated server the
    /// howl itself is animation nobody sees. Single player and a listen server are fine.
    /// </summary>
    public static Animator ModelAnimator(EntityAlive e)
    {
        if (e == null || e.emodel == null) return null;
        var model = e.emodel.GetModelTransform();
        return model == null ? null : model.GetComponent<Animator>();
    }

    /// <summary>
    /// Somewhere to put a summoned creature: <paramref name="distance"/> blocks from
    /// <paramref name="origin"/>, and OUT OF SIGHT of <paramref name="hideFrom"/> if one is given.
    ///
    /// Out of sight matters more than the distance does. A werewolf appearing from nothing in front
    /// of the player reads as a bug however good the animation is; the same werewolf arriving from
    /// behind, after a howl, reads as the howl having worked. So candidate directions are tried in
    /// a ring and the first one behind the player wins - "behind" being more than ViewCone degrees
    /// off the way they are facing.
    ///
    /// Ground height is terrain height where that agrees with where the summoner is standing.
    /// Inside a building it will not agree, and then the summoner's own footing is the better
    /// guess - it is on a floor, and so is the player it is fighting.
    /// </summary>
    public static Vector3 GroundNear(Vector3 origin, float distance, System.Random random,
                                     EntityAlive hideFrom = null)
    {
        // Half-angle of the cone counted as "in front of". 60 is wider than the game's own field
        // of view, deliberately: a spawn at the very edge of the screen is still a spawn the
        // player saw.
        const float ViewCone = 60f;
        const int Candidates = 12;

        var world = World;
        var start = (float)(random.NextDouble() * 360.0);
        var fallback = Vector3.zero;

        for (var i = 0; i < Candidates; i++)
        {
            // Walk the ring in even steps from a random start, so the choice is not biased to one
            // compass direction across many summons.
            var deg = start + i * (360f / Candidates);
            var rad = deg * Mathf.Deg2Rad;
            var spot = origin + new Vector3(Mathf.Cos(rad) * distance, 0f, Mathf.Sin(rad) * distance);

            if (world != null)
            {
                // Stand it on the ground, whatever height that is. This used to refuse a terrain
                // height more than 3 m from the summoner's and fall back to the summoner's own y,
                // which was survivable while summons landed 10 m away and became nonsense at 25:
                // over that distance the ground genuinely does rise and fall by more than three
                // metres, and taking the summoner's height instead buries the new creature in a
                // hillside or drops it out of the air. GetHeightAt is the ground; use it.
                var terrain = world.GetHeightAt(spot.x, spot.z) + 1f;
                if (terrain > 0f) spot.y = terrain;
            }

            if (i == 0) fallback = spot;
            if (hideFrom == null) return spot;

            var toSpot = spot - hideFrom.position;
            toSpot.y = 0f;
            if (toSpot.sqrMagnitude < 0.01f) continue;

            var facing = hideFrom.transform != null ? hideFrom.transform.forward : Vector3.forward;
            facing.y = 0f;
            if (Vector3.Angle(facing, toSpot) > ViewCone) return spot;
        }

        // Every direction was in front of them - they are spinning, or the summoner is between the
        // player and everywhere. Take the first candidate rather than refusing to summon: a visible
        // arrival beats a skill that silently does nothing.
        return fallback;
    }
}
