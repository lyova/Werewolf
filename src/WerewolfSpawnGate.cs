using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// What has to be true for a werewolf to APPEAR: late enough in the night, late enough in the game,
/// and not too many out already.
///
/// IT GATES ARRIVAL, NOT EXISTENCE. One already in the world is left alone - when the window shuts
/// at 03:00 the ones already out keep hunting until something kills them. An earlier version
/// removed them at the close of the window instead, and the difference matters in play: a creature
/// that evaporates on a timer is a creature you can wait out, and waiting it out is not meant to be
/// a tactic.
///
/// So the gate watches for NEW werewolves the BIOME SPAWNER produced. One of those, arriving while
/// the gate is shut, is removed the moment it turns up - unseen, because the game refuses to place
/// a biome spawn inside a player's view cone or within 50 m of one. Everything already known is
/// untouched whatever the clock says, and so is anything spawned from the console or called up by
/// a howl.
///
/// WHY IT IS CODE AND NOT XML. Biome spawning can express neither condition.
///
///   Time - spawning.xml has Day, Night and Any, and its Night is dusk to dawn, 22:00 to 04:00 at
///   the default 18-hour daylight. The window wanted is 22:00 to 03:00.
///
///   Progression - biome spawning has NO game stage concept whatsoever.
///   SpawnManagerBiomes.SpawnUpdate reads exactly two things, World.IsDaytime() and the game
///   difficulty setting. Vanilla gates its dangerous ambient spawns by PLACE instead: the
///   demolisher sits in ZombiesDowntown at 2% behind a downtown POI tag, and the zombie bear needs
///   the wasteland. Game stage exists only on the horde side, in gamestages.xml, where the
///   demolisher first appears at stage 147 and the zombie bear at 50.
///
/// A player's game stage is floor((level + min(daysAlive, level)) * difficultyBonus), with
/// difficultyBonus 1.2 by default and daysAlive capped at the player's level so that hiding in a
/// hole does not inflate it. Roughly, for someone whose level keeps pace with the days:
///
///   day 20 -> stage  48        day 40 -> stage  96        day 50 -> stage 120
///   day 30 -> stage  72        day 45 -> stage 108        day 60 -> stage 144
///
/// The world's stage here is the HIGHEST among the players online, which is how a server's
/// readiness is normally judged: one veteran means the world has moved on, whoever else is logged
/// in.
/// </summary>
public static class WerewolfSpawnGate
{
    /// <summary>The window new werewolves may arrive in, in game hours. It wraps midnight, so the
    /// test is "at or after the open hour OR before the close hour".</summary>
    public static int OpenHour = 22;
    public static int CloseHour = 3;

    /// <summary>
    /// The game stage the world has to reach before a werewolf may appear.
    ///
    /// 125 is roughly day 50 for a player levelling at the usual pace. It sits close under the
    /// demolisher, which vanilla first lets into a blood moon at stage 147, and well above the
    /// zombie bear's 50 - which is the right end of that range, because this hits harder than a
    /// zombie bear and calls two friends, and unlike the demolisher it turns up on an ordinary
    /// night rather than in a horde the player spent the day preparing for.
    ///
    /// Live-tunable: 'werewolf gate stage 60'.
    /// </summary>
    public static int MinGameStage = 125;

    /// <summary>
    /// How many werewolves the BIOME SPAWNER may have alive at once, across the whole world, and
    /// the game stage each step needs.
    ///
    /// WHY A CAP AT ALL. The game cannot express one. spawning.xml's maxcount is per spawn area - a
    /// 5x5 chunk, 80x80 m block of world - and a player standing still has several of those live
    /// around them, each with its own budget. Five turned up in one night on the first test.
    ///
    /// WHY IT GROWS. The gate at MinGameStage decides when they start appearing; this decides how
    /// many of them the world holds afterwards, and that should not be frozen at whatever suits the
    /// night they first arrive. A survivor at stage 250 is a different proposition from one at 125,
    /// and by then a lone werewolf is a chore rather than an event.
    ///
    /// The steps are cumulative in the ordinary way - the highest one whose stage the world has
    /// reached wins - and the first of them has to sit at MinGameStage, because that is the stage
    /// the first one can appear at all.
    ///
    /// FOR SCALE, since the grizzly is the obvious comparison: vanilla gives it 5 in 100 of the
    /// EnemyAnimalsSnow group, maxcount 1, respawndelay 2.6, snow only - and NO world-wide cap at
    /// all. So a snow biome can hold several grizzlies at once and routinely does. This creature
    /// gets a cap the grizzly does not, because it hits harder and calls friends.
    ///
    /// 4 at the bottom leaves enough of them wandering to be met rather than merely encountered,
    /// and cuts the churn: at 1, every new arrival displaced the one already out, so a player
    /// crossing areas met a stream of different werewolves - which reads as "they spawn constantly"
    /// even though only one was ever alive.
    ///
    /// The number to watch is not how many are about but how many CONVERGE. They chase, they are
    /// faster than a player, and a tail picked up over a few minutes of running arrives all at
    /// once - plus the two each of them can howl up. Twenty wandering the world at stage 250 is not
    /// twenty in one place, but it is a world where running has stopped being an answer.
    ///
    /// Only Biome spawns are counted and culled. A howled-up one is exempt (it has its own limit)
    /// and so is a console spawn, so 'se 0 animalWerewolf' still works whatever this says.
    ///
    /// When there are too many, the ones FURTHEST from any player go. The one being fought is by
    /// definition the closest, so it survives, and a creature vanishing 200 m away is a creature
    /// nobody was looking at.
    ///
    /// Live-tunable, and 'werewolf gate ambient <n>' pins it to a flat number, ignoring the curve.
    /// </summary>
    public static readonly (int stage, int limit)[] AmbientSteps =
    {
        (125, 4),
        (150, 10),
        (200, 15),
        (250, 20),
    };

    /// <summary>A flat cap that overrides the curve, or -1 to follow it. Set by
    /// 'werewolf gate ambient &lt;n&gt;' for testing.</summary>
    public static int AmbientOverride = -1;

    /// <summary>How many the biome spawner may have alive right now, given the world's game stage.
    /// Below the first step - which is the arrival gate itself - the answer is the first step's
    /// number, because at that point none can arrive anyway and the cull has nothing to do.</summary>
    public static int AmbientLimitNow()
    {
        if (AmbientOverride >= 0) return AmbientOverride;

        var stage = WorldGameStage();
        var limit = AmbientSteps[0].limit;
        foreach (var step in AmbientSteps)
        {
            if (stage >= step.stage) limit = step.limit;
        }
        return limit;
    }

    /// <summary>The curve as one line, for the console.</summary>
    public static string DescribeAmbient()
    {
        if (AmbientOverride >= 0) return "pinned at " + AmbientOverride + " (curve ignored)";

        var parts = new List<string>();
        foreach (var step in AmbientSteps) parts.Add("stage " + step.stage + " -> " + step.limit);
        return string.Join(", ", parts.ToArray()) + "; now " + AmbientLimitNow();
    }

    /// <summary>Off switch, so one can be spawned at noon on day one to look at it.
    /// 'werewolf gate off'.</summary>
    public static bool Enabled = true;

    const float CheckSeconds = 2f;
    static float nextCheck;

    /// <summary>Everything already let in. Membership is what makes this a gate on arrival rather
    /// than on existence: an id in here is never removed for the clock again.</summary>
    static readonly HashSet<int> known = new HashSet<int>();
    static readonly List<int> forgotten = new List<int>();

    public static bool HourIsOpen(int hour) => hour >= OpenHour || hour < CloseHour;

    /// <summary>Current game hour, 0-23, or -1 with no world.</summary>
    public static int Hour()
    {
        var world = WerewolfWorld.World;
        return world == null ? -1 : GameUtils.WorldTimeToHours(world.GetWorldTime());
    }

    /// <summary>The highest game stage among the players online, or -1 if there are none.</summary>
    public static int WorldGameStage()
    {
        var world = WerewolfWorld.World;
        if (world == null || world.Players == null || world.Players.list == null) return -1;

        var highest = -1;
        foreach (var p in world.Players.list)
        {
            if (p == null || p.IsDead()) continue;
            if (p.gameStage > highest) highest = p.gameStage;
        }
        return highest;
    }

    /// <summary>Why a werewolf may not ARRIVE right now, or null if one may.</summary>
    public static string Blocker()
    {
        var hour = Hour();
        if (hour < 0) return null;
        if (!HourIsOpen(hour))
        {
            return "it is " + hour.ToString("00", CultureInfo.InvariantCulture)
                   + ":00, outside " + OpenHour + ":00-0" + CloseHour + ":00";
        }

        var stage = WorldGameStage();
        if (stage >= 0 && stage < MinGameStage)
        {
            return "game stage " + stage + " is below " + MinGameStage;
        }
        return null;
    }

    public static void Tick()
    {
        if (!Enabled) return;
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + CheckSeconds;

        var world = WerewolfWorld.World;
        if (world == null) return;

        var live = WerewolfWorld.Live();
        var blocker = Blocker();

        // Turn away arrivals while the gate is shut. Two things are never turned away, and both
        // matter:
        //
        //   Anything already KNOWN, so one let in at 02:00 is still hunting at 04:00. That is the
        //   whole point of gating arrival rather than existence.
        //
        //   Anything the BIOME SPAWNER did not put there. This gate governs the world's own
        //   spawning, not deliberate acts. A werewolf typed in from the console is
        //   EnumSpawnerSource.Unknown and a summoned one is Dynamic; only Biome is the world
        //   deciding on its own. Without this, 'se 0 animalWerewolf' at noon spawned a creature
        //   that vanished in the same breath, which is not a gate doing its job - it is a gate
        //   arguing with the person testing it.
        var turnedAway = 0;
        foreach (var e in live)
        {
            if (known.Contains(e.entityId)) continue;
            if (blocker == null || e.GetSpawnerSource() != EnumSpawnerSource.Biome)
            {
                known.Add(e.entityId);
                continue;
            }
            world.RemoveEntity(e.entityId, EnumRemoveEntityReason.Despawned);
            turnedAway++;
        }
        if (turnedAway > 0)
        {
            Log.Out("[Werewolf] gate shut (" + blocker + "), turned away " + turnedAway + " arriving");
        }

        Forget(live);
        CullAmbient(world, live);
    }

    /// <summary>Drops the dead out of the known set, so it does not grow for the life of the
    /// session.</summary>
    static void Forget(List<EntityAlive> live)
    {
        if (known.Count <= live.Count) return;

        forgotten.Clear();
        foreach (var id in known)
        {
            var stillHere = false;
            foreach (var e in live)
            {
                if (e.entityId != id) continue;
                stillHere = true;
                break;
            }
            if (!stillHere) forgotten.Add(id);
        }
        foreach (var id in forgotten) known.Remove(id);
    }

    /// <summary>Holds the biome spawner to <see cref="AmbientLimit"/> werewolves world-wide.</summary>
    static void CullAmbient(World world, List<EntityAlive> live)
    {
        var limit = AmbientLimitNow();
        if (limit < 0) return;

        var ambient = new List<EntityAlive>();
        foreach (var e in live)
        {
            if (e.GetSpawnerSource() == EnumSpawnerSource.Biome) ambient.Add(e);
        }
        if (ambient.Count <= limit) return;

        // Furthest from any player first, so the cull takes the ones nobody can see and leaves
        // whichever is actually in play.
        ambient.Sort((a, b) => DistanceToNearestPlayer(b).CompareTo(DistanceToNearestPlayer(a)));

        var extra = ambient.Count - limit;
        for (var i = 0; i < extra; i++)
        {
            known.Remove(ambient[i].entityId);
            world.RemoveEntity(ambient[i].entityId, EnumRemoveEntityReason.Despawned);
        }
        Log.Out("[Werewolf] biome spawner had " + ambient.Count + " alive, limit is " + limit
                + " - removed the " + extra + " furthest from any player");
    }

    static float DistanceToNearestPlayer(EntityAlive e)
    {
        var p = WerewolfWorld.NearestPlayer(e.position);
        return p == null ? float.MaxValue : (p.position - e.position).sqrMagnitude;
    }
}
