using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

/// <summary>
/// The <c>werewolf</c> console command: retune the creature while standing in front of it, and
/// look at what its movement code is actually doing.
///
/// Why this exists at all. Balancing a chase means watching it, changing a number and watching it
/// again, and doing that through entityclasses.xml costs a restart every time. Worse, the game's
/// own path debugging cannot help here: <c>ai pathlines</c> flips GameManager.DebugAILines and the
/// path is drawn with UnityEngine.Debug.DrawLine, which only renders in the Unity editor and does
/// nothing at all in the shipped game. So the numbers and the diagnosis both had to come from
/// somewhere else, and this is it.
///
/// The command is registered by the game on its own: SdtdConsole scans loaded assemblies for
/// ConsoleCmdAbstract subclasses, so nothing has to be hooked.
///
/// Everything here is a LIVE tweak. It changes spawned entities and, where the value lives on the
/// EntityClass, the class every later spawn is built from - but it writes nothing to disk. Restart
/// the game and the XML is back in charge. Once a number is settled, put it in entityclasses.xml.
/// </summary>
public class ConsoleCmdWerewolf : ConsoleCmdAbstract
{
    /// <summary>Entity classes this command acts on: animalWerewolf and its test variants.</summary>
    const string ClassPrefix = "animalWerewolf";

    static bool traceOn;
    static float traceInterval = 1f;
    static float traceNext;

    public override string[] getCommands() => new[] { "werewolf", "ww" };

    public override string getDescription() => "tune and inspect the Werewolf mod's creature";

    public override string getHelp() =>
        "Tune the werewolf without restarting. Every change is live only - nothing is written to\n"
        + "entityclasses.xml, so a restart puts the XML back in charge.\n"
        + "\n"
        + "  werewolf                      what is spawned, and its current numbers\n"
        + "  werewolf speed <walk> <run>   metres per second, e.g. 'werewolf speed 1.8 7'\n"
        + "  werewolf raw <walk> <run>     the same but in the game's own unit, e.g. 'werewolf raw .55 2.1'\n"
        + "  werewolf turn <deg>           MaxTurnSpeed in degrees per second (default 280)\n"
        + "  werewolf trace [seconds]      start logging why it is blocked; 'werewolf trace off' stops\n"
        + "  werewolf kill                 remove every spawned werewolf\n"
        + "  werewolf gate [on|off]        the night + game stage gate; off keeps one alive in daylight\n"
        + "  werewolf gate stage <n>       the game stage the world needs before one may exist\n"
        + "  werewolf gate ambient <n>     pin the world cap to n, or 'curve' to follow game stage\n"
        + "  werewolf howl [on|off|now|reset]  the summon: state, switch, force one, clear the lock\n"
        + "  werewolf brawl [on|off|radius <m>] finish off the zombies on it before resuming the player\n"
        + "\n"
        + "About the m/s: the creature runs on root motion, so the ANIMATION carries the speed and\n"
        + "the XML number only chooses the gait. The walk clip travels "
        + WalkClipSpeed.ToString("0.0#", CultureInfo.InvariantCulture) + " m/s and the run "
        + RunClipSpeed.ToString("0.0#", CultureInfo.InvariantCulture) + " m/s, times the "
        + SizeScale.ToString("0.0#", CultureInfo.InvariantCulture) + " size scale.\n"
        + "Past the run there is a sprint node: the same clip again at "
        + SprintTimeScale.ToString("0.0#", CultureInfo.InvariantCulture) + "x playback, so the legs\n"
        + "cycle faster with it and the feet keep matching the ground. That caps the chase at "
        + (RunClipSpeed * SprintTimeScale * SizeScale).ToString("0.0", CultureInfo.InvariantCulture) + " m/s;\n"
        + "ask for more and you get exactly that. Raise SprintTimeScale in WerewolfAnimator.cs to\n"
        + "lift the cap, and remember 'werewolf turn' - speed over turn rate wants to stay near 0.7.";

    /// <summary>Metres per second the locomotion clips depict, measured off the planted feet by
    /// tools/blender-measure-stride.py and confirmed on import (AnimationClip.averageSpeed).</summary>
    const float WalkClipSpeed = 1.75f;
    const float RunClipSpeed = 6.10f;

    /// <summary>SizeScale from entityclasses.xml. Entity.SetScale scales the whole entity
    /// transform, so Animator.deltaPosition - and with it the ground speed - scales with it.</summary>
    const float SizeScale = 1.2f;

    /// <summary>The turning circle to aim for, in metres. A path is a voxel route - PathPoint holds
    /// integer coordinates - so a diagonal approach is a staircase of one-metre steps, and an
    /// animal whose circle is wider than a step overshoots every corner and weaves. The dire wolf
    /// sits at 0.68 m and runs straight; this is that number.</summary>
    const float TurningCircleMetres = 0.7f;

    /// <summary>Where the locomotion blend draws each gait, from WerewolfAnimator.cs.</summary>
    const float WalkForward = 0.1f;
    const float RunForward = 0.3f;

    /// <summary>The sprint node: the same run clip again, played SprintTimeScale times faster.
    /// Root motion scales with the playback rate, so this is the ceiling and the only thing that
    /// makes the creature outrun its own animation. Keep in step with WerewolfAnimator.cs.</summary>
    const float SprintForward = 0.6f;
    const float SprintTimeScale = 1.8f;

    /// <summary>
    /// XML speed number -> the Forward the animator sees. Read off the IL:
    /// SetMoveForwardWithModifiers puts the number in speedModifier, OnUpdateLive makes
    /// landMovementFactor = 0.1 * speedModifier, and the root-motion tail of MoveEntityHeaded
    /// sets speedForward = moveDirection.z * landMovementFactor * 2.5. Heading straight at a
    /// target moveDirection.z is 1, so Forward is a quarter of the number.
    /// </summary>
    const float ForwardPerUnit = 0.25f;

    /// <summary>Metres per second an XML speed number actually produces: it picks a point on the
    /// blend, and the blend mixes the clips travel in the same proportion as their poses.</summary>
    static float MetresPerSecond(float raw) => MetresPerSecondAtForward(raw * ForwardPerUnit);

    /// <summary>Metres per second the blend delivers at a given Forward. This is the real speed
    /// under root motion: the blend mixes the clips travel exactly as it mixes their poses.</summary>
    static float MetresPerSecondAtForward(float forward)
    {
        var sprintSpeed = RunClipSpeed * SprintTimeScale;
        float clip;
        if (forward <= 0f) clip = 0f;
        else if (forward <= WalkForward) clip = WalkClipSpeed * (forward / WalkForward);
        else if (forward <= RunForward) clip = Mathf.Lerp(WalkClipSpeed, RunClipSpeed, (forward - WalkForward) / (RunForward - WalkForward));
        else if (forward >= SprintForward) clip = sprintSpeed;  // the blend clamps at its last child
        else clip = Mathf.Lerp(RunClipSpeed, sprintSpeed, (forward - RunForward) / (SprintForward - RunForward));
        return clip * SizeScale;
    }

    /// <summary>The inverse, so the speed verb can take metres per second.</summary>
    static float RawForMetresPerSecond(float mps)
    {
        var sprintSpeed = RunClipSpeed * SprintTimeScale;
        var clip = mps / SizeScale;
        float forward;
        if (clip <= 0f) forward = 0f;
        else if (clip <= WalkClipSpeed) forward = WalkForward * (clip / WalkClipSpeed);
        else if (clip <= RunClipSpeed) forward = Mathf.Lerp(WalkForward, RunForward, (clip - WalkClipSpeed) / (RunClipSpeed - WalkClipSpeed));
        else if (clip >= sprintSpeed) forward = SprintForward;
        else forward = Mathf.Lerp(RunForward, SprintForward, (clip - RunClipSpeed) / (sprintSpeed - RunClipSpeed));
        return forward / ForwardPerUnit;
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        try
        {
            var verb = _params.Count > 0 ? _params[0].ToLowerInvariant() : "";
            switch (verb)
            {
                case "":
                case "info":
                    Info();
                    break;
                case "speed":
                    SetSpeed(_params, true);
                    break;
                case "raw":
                    SetSpeed(_params, false);
                    break;
                case "turn":
                    SetTurn(_params);
                    break;
                case "trace":
                    Trace(_params);
                    break;
                case "gate":
                    Gate(_params);
                    break;
                case "howl":
                    Howl(_params);
                    break;
                case "brawl":
                    Brawl(_params);
                    break;
                case "press":
                    Press(_params);
                    break;
                case "ears":
                    Ears(_params);
                    break;
                case "hop":
                    Hop(_params);
                    break;
                case "kill":
                    Kill();
                    break;
                default:
                    Out("unknown option '" + verb + "'");
                    Out(getHelp());
                    break;
            }
        }
        catch (Exception e)
        {
            Out("werewolf command failed: " + e.Message);
            Log.Error("[Werewolf] console command failed: " + e);
        }
    }

    // ---------------------------------------------------------------- verbs

    static void Info()
    {
        var live = Live();
        Out(live.Count + " werewolf/werewolves spawned"
            + (traceOn ? ", trace on every " + traceInterval.ToString("0.0", CultureInfo.InvariantCulture) + " s" : ""));

        foreach (var e in live)
        {
            var cc = e.m_characterController;
            var cls = EntityClass.list[e.entityClass];
            Out(string.Format(CultureInfo.InvariantCulture,
                "  id {0} {1}  at {2}  hp {3}/{4}",
                e.entityId, cls.entityClassName, e.position.ToCultureInvariantString("0.0"),
                (int)e.Health, (int)e.GetMaxHealth()));
            Out(string.Format(CultureInfo.InvariantCulture,
                "    walk {0:0.00} raw = {1:0.0} m/s   chase {2:0.00}/{3:0.00} raw = {4:0.0}/{5:0.0} m/s (day/night)",
                e.moveSpeed, MetresPerSecond(e.moveSpeed),
                e.moveSpeedAggro, e.moveSpeedAggroMax,
                MetresPerSecond(e.moveSpeedAggro), MetresPerSecond(e.moveSpeedAggroMax)));
            Out(string.Format(CultureInfo.InvariantCulture,
                "    controller r {0:0.00} h {1:0.00}   size scale {2:0.00}   turn {3:0} deg/s",
                cc != null ? cc.GetRadius() : -1f, cc != null ? cc.GetHeight() : -1f,
                cls.SizeScale, cls.MaxTurnSpeed));
            Out("    " + Blocked(e));
        }

        if (live.Count == 0) Out("  spawn one first: dm, then  se 0 animalWerewolf");

        var blocker = WerewolfSpawnGate.Blocker();
        Out("  gate " + (WerewolfSpawnGate.Enabled ? "on" : "OFF")
            + " (night " + WerewolfSpawnGate.OpenHour + ":00-0" + WerewolfSpawnGate.CloseHour + ":00"
            + ", game stage " + WerewolfSpawnGate.MinGameStage + "+): "
            + (blocker == null ? "clear" : "BLOCKED, " + blocker));
        Out("  howl " + (WerewolfHowl.Enabled ? "on" : "OFF")
            + ", summoned " + WerewolfHowl.Summoned + "/" + WerewolfHowl.PackLimit
            + (WerewolfHowl.Locked
                ? ", locked " + (WerewolfHowl.LockSecondsLeft / 60f).ToString("0.0", CultureInfo.InvariantCulture) + " min"
                : ""));
    }

    static void SetSpeed(List<string> p, bool inMetresPerSecond)
    {
        if (p.Count < 3 || !TryFloat(p[1], out var walk) || !TryFloat(p[2], out var run))
        {
            Out("need two numbers: werewolf " + p[0] + " <walk> <run>");
            return;
        }

        // The command's numbers are metres per second unless the verb was 'raw'; the game wants
        // its own unit either way.
        var rawWalk = inMetresPerSecond ? RawForMetresPerSecond(walk) : walk;
        var rawRun = inMetresPerSecond ? RawForMetresPerSecond(run) : run;
        // Vanilla gives the night value a small bump over the day one; keep that shape.
        var rawRunMax = rawRun * 1.05f;

        var live = Live();
        foreach (var e in live)
        {
            e.moveSpeed = rawWalk;
            e.moveSpeedNight = rawWalk;
            e.moveSpeedAggro = rawRun;
            e.moveSpeedAggroMax = rawRunMax;
        }

        // And the classes, so anything spawned from now on gets it too. The fields on EntityClass
        // are what CopyPropertiesFromEntityClass reads at spawn.
        var classes = 0;
        foreach (var cls in Classes())
        {
            cls.Properties.Values[EntityClass.PropMoveSpeed] = rawWalk.ToString("0.####", CultureInfo.InvariantCulture);
            cls.Properties.Values[EntityClass.PropMoveSpeedAggro] =
                rawRun.ToString("0.####", CultureInfo.InvariantCulture) + ", " +
                rawRunMax.ToString("0.####", CultureInfo.InvariantCulture);
            classes++;
        }

        Out(string.Format(CultureInfo.InvariantCulture,
            "walk {0:0.####} raw ({1:0.0} m/s), chase {2:0.####}, {3:0.####} raw ({4:0.0}/{5:0.0} m/s)",
            rawWalk, MetresPerSecond(rawWalk), rawRun, rawRunMax,
            MetresPerSecond(rawRun), MetresPerSecond(rawRunMax)));
        Out("applied to " + live.Count + " spawned and " + classes + " entity class(es)");
        Out("for entityclasses.xml:  MoveSpeed \"" + rawWalk.ToString("0.####", CultureInfo.InvariantCulture)
            + "\"   MoveSpeedAggro \"" + rawRun.ToString("0.####", CultureInfo.InvariantCulture)
            + ", " + rawRunMax.ToString("0.####", CultureInfo.InvariantCulture) + "\"");
        Out("NOTE root motion: the clips carry the travel. Walk "
            + (WalkClipSpeed * SizeScale).ToString("0.0", CultureInfo.InvariantCulture) + " m/s at raw "
            + (WalkForward / ForwardPerUnit).ToString("0.##", CultureInfo.InvariantCulture) + ", run "
            + (RunClipSpeed * SizeScale).ToString("0.0", CultureInfo.InvariantCulture) + " at raw "
            + (RunForward / ForwardPerUnit).ToString("0.##", CultureInfo.InvariantCulture) + ", ceiling "
            + (RunClipSpeed * SprintTimeScale * SizeScale).ToString("0.0", CultureInfo.InvariantCulture) + " at raw "
            + (SprintForward / ForwardPerUnit).ToString("0.##", CultureInfo.InvariantCulture) + ".");
        // Turning circle r = v / w, with w in RADIANS per second - so the degrees the XML wants is
        // (v / r) converted, not v / r raw. An earlier version printed the radians as if they were
        // degrees and advised 14 deg/s for a creature that needs 800.
        var turnWanted = MetresPerSecond(rawRun) / TurningCircleMetres * Mathf.Rad2Deg;
        Out("at that chase speed the turn rate wants to be about "
            + turnWanted.ToString("0", CultureInfo.InvariantCulture)
            + " deg/s to keep the turning circle near 0.7 m:  werewolf turn "
            + turnWanted.ToString("0", CultureInfo.InvariantCulture));
    }

    static void Gate(List<string> p)
    {
        if (p.Count > 1)
        {
            var arg = p[1].ToLowerInvariant();
            if (arg == "on") WerewolfSpawnGate.Enabled = true;
            else if (arg == "off") WerewolfSpawnGate.Enabled = false;
            else if (arg == "stage")
            {
                if (p.Count < 3 || !int.TryParse(p[2], out var stage)) { Out("need a number: werewolf gate stage 60"); return; }
                WerewolfSpawnGate.MinGameStage = stage;
            }
            else if (arg == "ambient")
            {
                if (p.Count < 3) { Out("need a number, or 'curve': werewolf gate ambient 2"); return; }
                if (p[2].ToLowerInvariant() == "curve") WerewolfSpawnGate.AmbientOverride = -1;
                else if (int.TryParse(p[2], out var cap)) WerewolfSpawnGate.AmbientOverride = cap;
                else { Out("need a number, or 'curve': werewolf gate ambient 2"); return; }
            }
            else { Out("say 'on', 'off', 'stage <n>' or 'ambient <n>'"); return; }
        }

        Out("gate " + (WerewolfSpawnGate.Enabled ? "ON" : "OFF")
            + ": night " + WerewolfSpawnGate.OpenHour + ":00 to 0" + WerewolfSpawnGate.CloseHour + ":00"
            + ", game stage " + WerewolfSpawnGate.MinGameStage + " or higher"
            + ", at most " + WerewolfSpawnGate.AmbientLimitNow() + " from the biome spawner");
        Out("  world cap by game stage: " + WerewolfSpawnGate.DescribeAmbient());
        Out("  the gate stops ARRIVALS only - one already out keeps hunting past "
            + WerewolfSpawnGate.CloseHour + ":00 until something kills it");

        var hour = WerewolfSpawnGate.Hour();
        var stageNow = WerewolfSpawnGate.WorldGameStage();
        if (hour >= 0)
        {
            Out("  now " + hour.ToString("00", CultureInfo.InvariantCulture) + ":00, world game stage "
                + (stageNow < 0 ? "unknown" : stageNow.ToString(CultureInfo.InvariantCulture))
                + " (the highest of the players online)");
        }

        var blocker = WerewolfSpawnGate.Blocker();
        Out(blocker == null ? "  nothing is blocking it" : "  BLOCKED: " + blocker);
        if (!WerewolfSpawnGate.Enabled) Out("  with the gate off nothing is removed - remember to put it back");
    }

    static void Howl(List<string> p)
    {
        if (p.Count > 1)
        {
            var arg = p[1].ToLowerInvariant();
            if (arg == "on") WerewolfHowl.Enabled = true;
            else if (arg == "off") WerewolfHowl.Enabled = false;
            else if (arg == "reset") { WerewolfHowl.Reset(); Out("pack counter and lock cleared"); }
            else if (arg == "now")
            {
                if (Live().Count == 0) { Out("nothing spawned. dm, then  se 0 animalWerewolf"); return; }
                if (WerewolfHowl.Locked)
                {
                    Out("locked for another " + (WerewolfHowl.LockSecondsLeft / 60f).ToString("0.0", CultureInfo.InvariantCulture)
                        + " min - 'werewolf howl reset' first if that is not what you wanted to see");
                    return;
                }
                WerewolfHowl.ForceNextRoll();
                Out("next roll forced. It still needs a werewolf that is FIGHTING something,"
                    + " so stand in front of one - it will not howl at nothing.");
                Out("  summoned so far " + WerewolfHowl.Summoned + " of " + WerewolfHowl.PackLimit
                    + "; the limit and the lock still apply");
                return;
            }
            else { Out("say 'on', 'off', 'now' or 'reset'"); return; }
        }

        Out("howl " + (WerewolfHowl.Enabled ? "ON" : "OFF")
            + ": a roll every " + WerewolfHowl.RollSeconds.ToString("0.#", CultureInfo.InvariantCulture)
            + " s per werewolf in a fight, " + (WerewolfHowl.Chance * 100f).ToString("0.#", CultureInfo.InvariantCulture)
            + "% each");
        Out("  summoned " + WerewolfHowl.Summoned + " of " + WerewolfHowl.PackLimit
            + (WerewolfHowl.Locked
                ? ", LOCKED for another " + (WerewolfHowl.LockSecondsLeft / 60f).ToString("0.0", CultureInfo.InvariantCulture) + " min"
                : ", not locked"));
        Out("  the counter is shared - it does not matter which werewolf did the calling");
    }

    /// <summary>
    /// werewolf brawl [on|off|radius N] - whether it finishes off the zombies on top of it before
    /// going back to the player, and how close they have to be to count.
    /// </summary>
    static void Brawl(List<string> p)
    {
        if (p.Count > 1)
        {
            var arg = p[1].ToLowerInvariant();
            if (arg == "on") WerewolfBrawl.Enabled = true;
            else if (arg == "off") WerewolfBrawl.Enabled = false;
            else if (arg == "radius")
            {
                if (p.Count < 3 || !TryFloat(p[2], out var metres)) { Out("need metres: werewolf brawl radius 3"); return; }
                WerewolfBrawl.BrawlRadius = metres;
            }
            else { Out("say 'on', 'off' or 'radius <metres>'"); return; }
        }

        Out("brawl " + (WerewolfBrawl.Enabled ? "ON" : "OFF")
            + ": once a ZOMBIE has hurt it, it works through every zombie within "
            + WerewolfBrawl.BrawlRadius.ToString("0.#", CultureInfo.InvariantCulture)
            + " m before going back to the player");
        Out("  it never starts a fight - walking past a zombie does nothing. The game's own");
        Out("  SetAsTargetIfHurt makes the first switch; this only stops it turning its back on");
        Out("  the rest of them the moment the first one drops.");

        var live = Live();
        if (live.Count == 0) { Out("  nothing spawned. dm, then  se 0 animalWerewolf"); return; }
        foreach (var e in live) Out("  id " + e.entityId + "  " + WerewolfBrawl.Describe(e));
    }

    /// <summary>
    /// werewolf press [on|off] - whether brief breaks in contact with a wall are allowed to wipe
    /// the BlockedTime that EAIBreakBlock waits 0.35 s for.
    /// </summary>
    static void Press(List<string> p)
    {
        if (p.Count > 1)
        {
            var arg = p[1].ToLowerInvariant();
            if (arg == "on") WerewolfPress.Enabled = true;
            else if (arg == "off") WerewolfPress.Enabled = false;
            else { Out("say 'on' or 'off'"); return; }
        }

        Out("press " + (WerewolfPress.Enabled ? "ON" : "OFF")
            + ": contact with a wall survives a short break instead of resetting to zero");
        Out("  EAIBreakBlock needs BlockedTime >= 0.35, and the game only adds to it on frames where");
        Out("  the creature is blocked - one clear frame and the count starts over. A zombie leaning");
        Out("  on a wall manages that; one running at 7 m/s hits, rebounds and hits again.");
        Out("  It is only ever raised while in contact THIS tick, and never above the contact seen.");

        var live = Live();
        if (live.Count == 0) { Out("  nothing spawned. dm, then  se 0 animalWerewolf"); return; }
        foreach (var e in live) Out("  id " + e.entityId + "  " + WerewolfPress.Describe(e));
    }

    /// <summary>
    /// werewolf ears [on|off|range N] - whether hearing a player turns into hunting one.
    /// </summary>
    static void Ears(List<string> p)
    {
        if (p.Count > 1)
        {
            var arg = p[1].ToLowerInvariant();
            if (arg == "on") WerewolfEars.Enabled = true;
            else if (arg == "off") WerewolfEars.Enabled = false;
            else if (arg == "range")
            {
                if (p.Count < 3 || !TryFloat(p[2], out var metres)) { Out("need metres: werewolf ears range 20"); return; }
                WerewolfEars.Range = metres;
            }
            else { Out("say 'on', 'off' or 'range <metres>'"); return; }
        }

        Out("ears " + (WerewolfEars.Enabled ? "ON" : "OFF")
            + ": a noise it is already investigating becomes a hunt, if a player is within "
            + WerewolfEars.Range.ToString("0.#", CultureInfo.InvariantCulture) + " m");
        Out("  vanilla hearing never produces a target - SeekNoise only sets a place to go and sniff.");
        Out("  Without a target EAIDestroyArea cannot run at all, so a creature that can hear you");
        Out("  through a wall but not see you had no way to come through it.");
        Out("  It never overrides an existing target, and the target lasts 600 ticks - the same 30 s");
        Out("  the game grants on sight.");

        var live = Live();
        if (live.Count == 0) { Out("  nothing spawned. dm, then  se 0 animalWerewolf"); return; }
        foreach (var e in live) Out("  id " + e.entityId + "  " + WerewolfEars.Describe(e));
    }

    static void SetTurn(List<string> p)
    {
        if (p.Count < 2 || !TryFloat(p[1], out var degrees))
        {
            Out("need a number: werewolf turn <degrees per second>, vanilla animals use 280");
            return;
        }

        var n = 0;
        foreach (var cls in Classes())
        {
            cls.MaxTurnSpeed = degrees;
            n++;
        }
        Out("MaxTurnSpeed " + degrees.ToString("0.#", CultureInfo.InvariantCulture) + " on " + n + " class(es)");
        Out("a faster turn is the other half of the overshoot problem: at v m/s and w deg/s the");
        Out("tightest circle it can hold is v / (w in radians) metres.");
    }

    static void Trace(List<string> p)
    {
        if (p.Count > 1 && (p[1] == "off" || p[1] == "0"))
        {
            traceOn = false;
            Out("trace off");
            return;
        }
        if (p.Count > 1 && TryFloat(p[1], out var seconds) && seconds > 0f) traceInterval = seconds;
        traceOn = true;
        traceNext = 0f;
        Out("trace on, every " + traceInterval.ToString("0.0", CultureInfo.InvariantCulture)
            + " s, into the console and the log. 'werewolf trace off' stops it.");
        Out("what to watch: 'blocked' naming something means the move helper thinks it cannot go");
        Out("straight, and that is the swerve. All clear while it still weaves means the path");
        Out("itself is bending, or it is simply overshooting you.");
    }


    static void Kill()
    {
        var live = Live();
        foreach (var e in live) e.MarkToUnload();
        Out("removed " + live.Count);
    }

    // ---------------------------------------------------------------- trace

    /// <summary>
    /// Called from the mod's update hook. Prints, per werewolf, the two things that decide whether
    /// it runs straight: what the move helper currently considers blocking, and how fast it is
    /// actually travelling against how fast it is allowed to.
    /// </summary>
    public static void Tick()
    {
        if (!traceOn) return;
        if (Time.time < traceNext) return;
        traceNext = Time.time + traceInterval;

        // This runs from ModEvents.GameUpdate. An exception escaping here would take the handler
        // with it and the trace would simply stop, which is exactly what happened the first time:
        // one line and then silence, with nothing in the log to say why.
        try
        {
            TickInner();
        }
        catch (Exception e)
        {
            Log.Error("[Werewolf] trace failed, turning it off: " + e);
            SdtdConsole.Instance.Output("[Werewolf] trace failed and was turned off - see the log");
            traceOn = false;
        }
    }

    static void TickInner()
    {
        var live = Live();
        if (live.Count == 0)
        {
            // Silence here reads as "the trace is broken". Say what is actually true.
            Out("trace: nothing spawned. dm, then  se 0 animalWerewolf");
            return;
        }

        foreach (var e in live)
        {
            // Under root motion the ground speed is whatever the blend at this Forward travels,
            // not the parameter scaled by anything. Strafe cannot add to it: there is no sideways
            // clip to blend, so it never turns into displacement.
            var speed = MetresPerSecondAtForward(e.speedForward);
            var target = e.GetAttackTarget();
            Out(string.Format(CultureInfo.InvariantCulture,
                "[ww {0}] {1}  Forward {2:0.00} Strafe {3:0.00} -> ~{4:0.0} m/s   target {5}   {6}",
                e.entityId,
                e.IsSleeping ? "asleep" : (target != null ? "chasing" : "idle"),
                e.speedForward, e.speedStrafe, speed,
                target != null ? target.EntityName + " at " + Distance(e, target) : "none",
                Blocked(e)));

            Out("        " + Aim(e, target));
            if (target != null) Out("        " + Straightness(e, target));
            Out("        " + Combat(e));
            Out("        " + Head(e, target));
            if (target != null) Out("        " + Reach(e, target));
            Out("        " + WerewolfAim.Describe(e));
            // Printed unconditionally. Both of these used to be hidden behind "only if it looks
            // blocked" and "only if it has a target", which meant they went quiet in precisely the
            // situation worth reporting - a creature that is neither blocked nor holding a target
            // is the failure, not a case to skip.
            Out("        " + Motion(e));
            Out("        " + WerewolfBlocker.Describe(e));
            Out("        " + WerewolfHop.Describe(e));
            Out("        " + Body(e));
            Out("        " + WhyNotBreaking(e));
            Out("        " + WerewolfBrawl.Describe(e));
            Out("        " + WerewolfPack.Describe());
            Out("        " + WerewolfEars.Describe(e));
            Out("        " + WerewolfPress.Describe(e));
            Out("        " + Tasks(e));
        }
    }

    /// <summary>
    /// The one number that says how badly it fails to run at you, and the two that explain it.
    ///
    /// "off by N deg" is the angle between the direction it is actually travelling and the straight
    /// line to the target. Near zero is a clean charge. Persistently large is the whole complaint,
    /// measured instead of eyeballed.
    ///
    /// Then the two known reasons it can be large even when nothing is blocking: the target's own
    /// speed, because the chase aims at target + velocity * 6 and a running player throws the aim
    /// point metres ahead; and the staircase, because a voxel path can only step along an axis, so
    /// a diagonal approach is a flight of stairs and the creature is following it faithfully.
    /// </summary>
    static string Straightness(EntityAlive e, Entity target)
    {
        var toTarget = new Vector2(target.position.x - e.position.x, target.position.z - e.position.z);
        var travel = new Vector2(e.speedStrafe, e.speedForward);

        var parts = new List<string>();
        if (toTarget.sqrMagnitude > 0.01f && travel.sqrMagnitude > 0.0001f)
        {
            // Travel is in the entity's own frame, so rotate it into the world by the entity yaw.
            var yaw = e.rotation.y * Mathf.Deg2Rad;
            var world = new Vector2(
                travel.y * Mathf.Sin(yaw) + travel.x * Mathf.Cos(yaw),
                travel.y * Mathf.Cos(yaw) - travel.x * Mathf.Sin(yaw));
            var angle = Vector2.Angle(world.normalized, toTarget.normalized);
            parts.Add("off by " + angle.ToString("0", CultureInfo.InvariantCulture) + " deg from the TARGET");

            // And the same angle measured against the WAYPOINT, which is the one that decides
            // whether it can follow its path at all. Under root motion the clip pushes the creature
            // along its facing, so if it is aimed at the target while the path says go somewhere
            // else, it presses into whatever lies between and never advances a node. The target
            // angle alone cannot show that - it reads a perfect 0 degrees while the creature is
            // walking into a wall.
            var mh2 = e.moveHelper;
            if (mh2 != null)
            {
                var toWay = new Vector2(mh2.moveToPos.x - e.position.x, mh2.moveToPos.z - e.position.z);
                if (toWay.sqrMagnitude > 0.0001f)
                {
                    var wayAngle = Vector2.Angle(world.normalized, toWay.normalized);
                    parts.Add("off by " + wayAngle.ToString("0", CultureInfo.InvariantCulture) + " deg from the WAYPOINT");
                }
            }
        }

        var targetSpeed = target.speedForward * target.speedForward + target.speedStrafe * target.speedStrafe;
        parts.Add("target moving ~" + (Mathf.Sqrt(targetSpeed) * 10f).ToString("0.0", CultureInfo.InvariantCulture)
                  + " m/s (its speed x 6 is where the chase aims)");

        return string.Join("   ", parts.ToArray());
    }

    /// <summary>
    /// Where the creature is actually steering, against where the target is.
    ///
    /// This is the line that matters for the swerving, because the chase does NOT aim at the
    /// target. EAIApproachAndAttackTarget.GetMoveToLocation returns
    /// <c>target.position + targetVelocity * 6</c> - a six-second lead. A target that moves at all
    /// swings that aim point metres to the side, and the creature curves toward it rather than
    /// toward the player. "lead" below is how far the aim sits from the target: near zero while
    /// you stand still, large while you move, and a large lead with a clear path is the swerve
    /// explained without any obstacle being involved.
    ///
    /// "path" is the node count the navigator is following. Nodes appearing and vanishing every
    /// tick means it is re-planning constantly, which is a different fault with the same look.
    /// </summary>
    static string Aim(EntityAlive e, Entity target)
    {
        var mh = e.moveHelper;
        if (mh == null) return "no move helper";

        var parts = new List<string>();

        // Both positions in BLOCK coordinates, because that is the frame the path nodes are in and
        // the only frame in which "which block is between us" is answerable. An earlier version of
        // this printed neither, and called moveToPos a predicted lead on the target - moveToPos is
        // simply the current waypoint, so that label invented a mechanism that does not exist.
        parts.Add("at " + World.worldToBlockPos(e.position));
        if (target != null) parts.Add("target at " + World.worldToBlockPos(target.position));
        parts.Add("waypoint " + Flat(mh.moveToPos));
        parts.Add("moveToDist " + mh.moveToDistance.ToString("0.0", CultureInfo.InvariantCulture));
        if (mh.moveToFailCnt > 0) parts.Add("failed moves " + mh.moveToFailCnt);

        var nav = e.navigator;
        var path = nav != null ? nav.currentPath : null;
        parts.Add(path != null
            ? "path " + path.NodeCountRemaining() + " of " + path.pathLength + " nodes left"
            : "path none");

        var line = string.Join("   ", parts.ToArray());

        // The nodes themselves. PathPoint stores INTEGER x/y/z - the path is a voxel grid route,
        // one block per step - so a diagonal approach is a staircase by construction. Printing the
        // next few settles the question the trace exists to answer: whether the creature is
        // faithfully following a jagged path, or wandering off a straight one.
        if (path != null)
        {
            var nodes = new List<string>();
            var from = path.getCurrentPathIndex();
            for (var i = from; i < path.pathLength && nodes.Count < 6; i++)
            {
                var p = path.getPathPointFromIndex(i);
                if (p == null) continue;
                nodes.Add(p.x + "," + p.y + "," + p.z);
            }
            if (nodes.Count > 0) line += "\n        next nodes " + string.Join(" -> ", nodes.ToArray());
        }

        return line;
    }

    static string Flat(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.0},{1:0.0})", v.x, v.z);

    /// <summary>
    /// What the animator is doing, which is the other half of "it stands there and does nothing".
    ///
    /// The AI asks the avatar controller whether an attack is still playing before it does anything
    /// else, and <c>AvatarAnimalController.IsAnimationAttackPlaying</c> answers from two things: a
    /// half-second timer, and whether the current state on layer 0 carries the <c>Attack</c> tag.
    /// A controller that enters a tagged state and never leaves therefore freezes the AI - the
    /// creature stops, waits for an attack that is already "playing" forever, and never swings.
    /// This line shows the state, its tag, how far through it is, and what the AI concludes.
    /// </summary>
    /// <summary>
    /// Where the muzzle actually points on a LIVE creature, against where the clips say it should.
    ///
    /// Reported in game: the head sits off to one side while the werewolf chases, and straight
    /// ahead while it stands or walks. Sampling the clips in the editor says otherwise - idle,
    /// walk and run all hold the head within a couple of degrees of the same direction - so either
    /// something at runtime is turning it, or the two are not measuring the same thing. This is the
    /// same measurement taken where the complaint is.
    ///
    /// "vs clips" is that editor baseline. A live yaw near it means the animation is being played
    /// as authored and the head is where the artist put it. A live yaw well off it means something
    /// else is rotating the bone, and then the additive pain layer is the first suspect - its
    /// weight is on the line above.
    ///
    /// "to target" is the separate question of whether the head happens to point at what it is
    /// chasing, which is what the eye actually judges.
    /// </summary>
    const float HeadYawInClips = -90f;

    static string Head(EntityAlive e, EntityAlive target)
    {
        if (e.emodel == null) return "head: no model";
        var model = e.emodel.GetModelTransform();
        if (model == null) return "head: no model transform";

        var head = FindChild(model, "Head");
        if (head == null) return "head: no Head bone";

        var local = Quaternion.Inverse(model.rotation) * head.rotation;
        var facing = local * Vector3.forward;
        var yaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
        var off = Mathf.DeltaAngle(HeadYawInClips, yaw);

        var line = "head: yaw " + yaw.ToString("0.0", CultureInfo.InvariantCulture)
                   + " vs clips " + HeadYawInClips.ToString("0", CultureInfo.InvariantCulture)
                   + " -> off by " + off.ToString("0.0", CultureInfo.InvariantCulture) + " deg";

        if (target != null)
        {
            // The world direction the muzzle points, against the world direction to the target.
            var muzzle = head.rotation * Vector3.forward;
            muzzle.y = 0f;
            var toTarget = target.position - e.position;
            toTarget.y = 0f;
            if (muzzle.sqrMagnitude > 0.001f && toTarget.sqrMagnitude > 0.001f)
            {
                line += ", to target " + Vector3.Angle(muzzle, toTarget).ToString("0", CultureInfo.InvariantCulture) + " deg";
            }
        }
        return line;
    }

    static Transform FindChild(Transform root, string name)
    {
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++)
        {
            var hit = FindChild(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    static string Combat(EntityAlive e)
    {
        var ac = e.emodel != null ? e.emodel.avatarController : null;
        var anim = ac != null ? ac.GetAnimator() : null;
        if (anim == null) return "anim: none";

        var st = anim.GetCurrentAnimatorStateInfo(0);
        var parts = new List<string>
        {
            "anim tag " + TagName(st.tagHash),
            "t=" + st.normalizedTime.ToString("0.00", CultureInfo.InvariantCulture),
        };
        if (anim.IsInTransition(0)) parts.Add("in transition");
        parts.Add("attackPlaying=" + ac.IsAnimationAttackPlaying());
        parts.Add("Forward=" + anim.GetFloat("Forward").ToString("0.00", CultureInfo.InvariantCulture));
        parts.Add("Strafe=" + anim.GetFloat("Strafe").ToString("0.00", CultureInfo.InvariantCulture));
        parts.Add("Attack=" + anim.GetInteger("Attack"));
        parts.Add("IsAlive=" + anim.GetBool("IsAlive"));

        if (anim.layerCount > 1)
        {
            var pain = anim.GetCurrentAnimatorStateInfo(1);
            parts.Add("pain layer tag " + TagName(pain.tagHash)
                      + " w=" + anim.GetLayerWeight(1).ToString("0.00", CultureInfo.InvariantCulture));
        }

        return string.Join("   ", parts.ToArray());
    }

    static readonly string[] KnownTags = { "Attack", "Death", "Hit", "AttackStart", "AttackReady", "Stun" };

    static string TagName(int hash)
    {
        if (hash == 0) return "(none)";
        foreach (var t in KnownTags)
        {
            if (Animator.StringToHash(t) == hash) return t;
        }
        return hash.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>What EntityMoveHelper thinks is in the way, in words.</summary>
    static string Blocked(EntityAlive e)
    {
        var mh = e.moveHelper;
        if (mh == null) return "no move helper";

        var reasons = new List<string>();
        if (mh.BlockedFlags != 0) reasons.Add("BlockedFlags=" + mh.BlockedFlags);
        if (mh.BlockedEntity != null) reasons.Add("blocked by " + mh.BlockedEntity.EntityName);
        if (mh.IsUnreachableAbove) reasons.Add("unreachable above");
        if (mh.IsUnreachableSide) reasons.Add("unreachable to the side");
        if (mh.BlockedTime > 0f) reasons.Add("blocked for " + mh.BlockedTime.ToString("0.0", CultureInfo.InvariantCulture) + " s");
        if (e.navigator != null && e.navigator.noPathAndNotPlanningOne()) reasons.Add("no path");

        return reasons.Count == 0 ? "clear" : "blocked: " + string.Join(", ", reasons.ToArray());
    }

    /// <summary>
    /// EAIBreakBlock.CanExecute, condition by condition, on a live creature.
    ///
    /// "runs at the wall and will not break it" has at least six separate ways to happen and they
    /// are indistinguishable from outside. This prints which one is failing rather than leaving it
    /// to be guessed at from a screenshot. Stand behind whatever it refuses to break and read the
    /// line: everything marked ok, and it would be swinging.
    ///
    /// The last one is the subtle one. The move helper probes for obstacles with several rays at
    /// different heights and keeps ONE of them in HitInfo; the task then refuses if the block that
    /// ray landed on is air. A wall with a hole in it at the height the winning ray happens to use
    /// therefore reads as "nothing to break" even though the creature is plainly stuck against it.
    /// </summary>
    /// <summary>
    /// Which AI tasks are actually executing, and what the digging one has decided to do.
    ///
    /// Everything else in this trace describes the conditions we hand the AI. This says what the
    /// AI did with them, which is the one thing that was being inferred rather than read - three
    /// rounds were spent tuning a trigger without ever confirming that the task it was meant to
    /// wake up had run at all.
    ///
    /// EAIDestroyArea.ToString() carries its own state machine position (FindPos, FindPath,
    /// HasPath, Attack and the rest) plus findCount, so a task that is looping in FindPos looks
    /// nothing like one that has reached Attack. The game has a LogDestroy for exactly this and
    /// never calls it in the shipped build, hence reading the task directly.
    /// </summary>
    static string Tasks(EntityAlive e)
    {
        if (e.aiManager == null || e.aiManager.tasks == null) return "ai: no task list";
        var running = e.aiManager.tasks.GetExecutingTasks();
        if (running == null || running.Count == 0) return "ai: nothing executing";

        var parts = new List<string>();
        foreach (var entry in running)
        {
            if (entry == null || entry.action == null) continue;
            var task = entry.action;
            var name = task.GetTypeName();
            parts.Add(task is EAIDestroyArea ? name + " [" + task + "]" : name);
        }
        return "ai: " + string.Join(", ", parts.ToArray());
    }

    /// <summary>
    /// The distances an attack is actually judged on, so "it will not hit me" stops being a guess.
    ///
    /// Two different spans matter and they are not the same number. EAIApproachAndAttackTarget
    /// decides whether to swing at all from centre to centre, but the swing itself goes through
    /// EntityAlive.Attack -> UseHoldingItem, and the item's own test runs from the HEAD - the same
    /// getHeadPosition() that EModelBase.GetChestPosition is derived from. On a tall creature
    /// standing over a crouched-height chest those two differ by more than the reach itself.
    ///
    /// Range on meleeHandAnimalWerewolf is 2.2. If the head to
    /// chest span here reads above that while centre to centre reads below, the reach number is
    /// being measured against the wrong span and that is the bug.
    /// </summary>
    static string Reach(EntityAlive e, Entity target)
    {
        var centres = (target.position - e.position).magnitude;
        var flat = new Vector2(target.position.x - e.position.x, target.position.z - e.position.z).magnitude;

        // The ACTUAL melee probe, not the head bone. ItemActionAttack.Hit takes
        // EntityAlive.GetLookRay, whose origin is position + GetEyeHeight() and whose direction is
        // GetLookVector() off Entity.rotation. An earlier version of this line measured head bone
        // to chest, which is a span the attack never consults.
        var ray = e.GetLookRay();
        var chest = target.getChestPosition();

        // How far the probe passes above or below the chest at the target's range. This is the
        // number Sphere has to cover; anything larger than the sphere radius is a clean miss.
        var toChest = chest - ray.origin;
        var along = Vector3.Dot(toChest, ray.direction);
        var miss = (toChest - ray.direction * along).magnitude;

        return "reach: centres " + centres.ToString("0.00", CultureInfo.InvariantCulture)
               + " m, flat " + flat.ToString("0.00", CultureInfo.InvariantCulture)
               + " m (Range 2.2)   ray from y " + ray.origin.y.ToString("0.00", CultureInfo.InvariantCulture)
               + " pitch " + (-Mathf.Asin(Mathf.Clamp(ray.direction.normalized.y, -1f, 1f)) * Mathf.Rad2Deg).ToString("0.0", CultureInfo.InvariantCulture)
               + " deg, chest y " + chest.y.ToString("0.00", CultureInfo.InvariantCulture)
               + "   passes " + miss.ToString("0.00", CultureInfo.InvariantCulture)
               + " m off the chest (Sphere .3 covers it)"
               + "   in front: " + (e.IsInFrontOfMe(chest) ? "yes" : "NO");
    }

    /// <summary>
    /// The creature's physical footprint, which is a capsule and has nothing to do with how long
    /// the model looks mid-stride.
    ///
    /// The question this answers: can it physically stand on a one-block ledge with its chest
    /// against a wall? The mover works in capsule radius, not model bounds - EAIBreakBlock.CanExecute
    /// literally computes GetRadius() + 0.7 for its own reach test. If the radius is much over half
    /// a metre, a one-metre pad in front of a wall has no room for the capsule at all, so the
    /// creature stops a block short, never touches the wall, and BlockedFlags stays 0 - which is
    /// exactly the shape of the trace.
    ///
    /// SizeScale 1.2 scales the whole entity transform including colliders, so this is 20 per cent
    /// wider than whatever the prefab was authored at.
    /// </summary>
    static string Body(EntityAlive e)
    {
        var cc = e.m_characterController;
        if (cc == null) return "body: no character controller";
        var radius = cc.GetRadius();
        // physicsBaseHeight is the one that decides whether crouching happens at all:
        // EntityAlive.CrouchHeightFixedUpdate returns immediately unless crouchType is set AND
        // physicsBaseHeight is over 1.3. physicsHeight is what it then eases DOWN to fit a ceiling,
        // so base staying equal to current means no duck was attempted.
        var crouch = e.crouchType != 0
            ? "crouchType " + e.crouchType + ", baseHeight " + e.physicsBaseHeight.ToString("0.00", CultureInfo.InvariantCulture)
              + (e.physicsBaseHeight > 1.3f ? " ok" : " TOO LOW - crouching never runs")
              + ", bend " + e.crouchBendPer.ToString("0.00", CultureInfo.InvariantCulture)
              + ", walkType " + e.walkType + (e.walkType == 8 ? " (crawler)" : "")
            : "crouchType 0 - cannot duck at all";

        return "body: capsule radius " + radius.ToString("0.00", CultureInfo.InvariantCulture)
               + " m (needs " + (radius * 2f).ToString("0.00", CultureInfo.InvariantCulture)
               + " m of floor to stand clear), physicsHeight " + e.physicsHeight.ToString("0.00", CultureInfo.InvariantCulture)
               + ", height " + e.height.ToString("0.00", CultureInfo.InvariantCulture)
               + ", breakBlock reach radius+0.7 = " + (radius + 0.7f).ToString("0.00", CultureInfo.InvariantCulture) + " m"
               + "\n        crouch: " + crouch;
    }

    /// <summary>
    /// Which animator state it is actually in, and how far it has actually travelled.
    ///
    /// Everything else in this trace reports what the AI WANTS. This reports what the body did.
    /// The distinction matters because Forward is a blend parameter, not a fact: under root motion
    /// the CLIP carries the movement, and only two of this creature's clips carry any -
    /// Walk_4_Legs at 1.75 m/s and Run_4_Legs at 6.10. Idle, Agr, Attack_L/R, Jump, Landing, Fall
    /// and Hit are all authored in place at 0.00. Sit in one of those and the creature animates
    /// beautifully while going nowhere, with Forward still reading 0.30 and the AI still insisting
    /// it is running at 7.3 m/s.
    ///
    /// Travelled is measured between calls, so at a one-second trace interval a creature genuinely
    /// running should show metres, not centimetres.
    /// </summary>
    static readonly Dictionary<int, Vector3> lastSeenAt = new Dictionary<int, Vector3>();
    static readonly Dictionary<int, float> lastSeenTime = new Dictionary<int, float>();

    /// <summary>werewolf hop [on|off] - whether pointless jumps on level ground are refused.</summary>
    static void Hop(List<string> p)
    {
        if (p.Count > 1)
        {
            var arg = p[1].ToLowerInvariant();
            if (arg == "on") WerewolfHop.Enabled = true;
            else if (arg == "off") WerewolfHop.Enabled = false;
            else { Out("say 'on' or 'off'"); return; }
        }

        Out("hop suppression " + (WerewolfHop.Enabled ? "ON" : "OFF")
            + ": a jump is refused when the next path node is less than "
            + WerewolfHop.ClimbNeeded.ToString("0.##", CultureInfo.InvariantCulture) + " m above it");
        Out("  Jump, Fall and Landing are all authored in place - rootMotion 0.00 - so a hop costs");
        Out("  about three seconds of no movement at all. On the flat there is nothing to gain from");
        Out("  one, and repeating it is what kept this creature at 0.4 m/s while the AI believed 7.3.");

        var live = Live();
        if (live.Count == 0) { Out("  nothing spawned. dm, then  se 0 animalWerewolf"); return; }
        foreach (var e in live) Out("  id " + e.entityId + "  " + WerewolfHop.Describe(e));
    }

    static string Motion(EntityAlive e)
    {
        var parts = new List<string>();

        var anim = WerewolfWorld.ModelAnimator(e);
        if (anim == null) parts.Add("state: no animator");
        else
        {
            var info = anim.GetCurrentAnimatorStateInfo(0);
            // Every state WerewolfAnimator.cs creates on layer 0, by its exact name. The first
            // version of this list had Attack_L, Attack_R and Idle in it - none of which exist -
            // and reported half the samples as "unknown" while the creature was swinging at air.
            var named = new[] { "Move", "IdleBreak", "Jump", "Fall", "Landing", "AttackL", "AttackR", "Pain", "Death", "Howl", "Eat", "EatOut", "Swim", "Empty" };
            var name = "unknown";
            foreach (var candidate in named)
            {
                if (!info.IsName(candidate)) continue;
                name = candidate;
                break;
            }
            parts.Add("state: " + name + " t=" + info.normalizedTime.ToString("0.00", CultureInfo.InvariantCulture)
                      + (anim.IsInTransition(0) ? " (in transition)" : "")
                      + (name == "Move" || name == "Swim" ? "" : "  <- NO ROOT MOTION IN THIS CLIP"));

            // What the animator is PRODUCING, independent of what the entity does with it.
            // Animator.velocity is the root-motion velocity of the last evaluated frame and is not
            // consumed by anything, so it can be read at any moment. Against "travelled" below it
            // splits the remaining question cleanly: clip says 7 m/s and body does 0.3 means the
            // displacement is being thrown away between OnAnimatorMove and MoveEntityHeaded; clip
            // says 0 means the blend is not where Forward claims it is.
            parts.Add("clip produces " + anim.velocity.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s"
                      + " (root motion apply " + (anim.applyRootMotion ? "on" : "OFF") + ")");
            parts.Add("accumulated " + e.accumulatedRootMotion.magnitude.ToString("0.000", CultureInfo.InvariantCulture) + " m pending");
        }

        Vector3 before;
        float when;
        var now = Time.time;
        if (lastSeenAt.TryGetValue(e.entityId, out before) && lastSeenTime.TryGetValue(e.entityId, out when) && now > when)
        {
            var moved = (e.position - before).magnitude;
            var seconds = now - when;
            parts.Add("travelled " + moved.ToString("0.00", CultureInfo.InvariantCulture) + " m in "
                      + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s = "
                      + (moved / seconds).ToString("0.0", CultureInfo.InvariantCulture) + " m/s ACTUAL");
        }
        lastSeenAt[e.entityId] = e.position;
        lastSeenTime[e.entityId] = now;

        // What the CHARACTER CONTROLLER hit this frame - the one report in this whole trace that
        // does not come from the mover's own raycasts. BlockedFlags is recomputed on a four-tick
        // cycle and was 0 in every sample here; this is the physics engine's answer to the same
        // question, and a creature grinding along a surface it cannot see will show it.
        // The three booleans rather than Entity.collisionFlags itself: that field's type lives in
        // UnityEngine.PhysicsModule, which this project does not reference, and the booleans say
        // the same thing.
        parts.Add("controller:"
                  + (e.isCollidedHorizontally ? " HIT SIDEWAYS" : "")
                  + (e.isCollidedVertically ? " hit up/down" : "")
                  + (!e.isCollided ? " nothing touched" : ""));

        // The last two places a requested move can be lost between the entity and the ground,
        // neither of which raises a collision flag. ccEntityCollisionStart multiplies the request
        // by motionMultiplier when isMotionSlowedDown is set - the game's slow-down for walking
        // through certain blocks. ccEntityCollisionResults then records projectedMove: how much of
        // the requested x/z actually happened, 0..1. And the kinematic motor can refuse to move a
        // character it does not consider stably grounded, snapping it back each frame instead.
        parts.Add("slowed " + (e.isMotionSlowedDown ? "YES x" + e.motionMultiplier.ToString("0.00", CultureInfo.InvariantCulture) : "no")
                  + ", achieved " + (e.projectedMove * 100f).ToString("0", CultureInfo.InvariantCulture) + "% of requested move"
                  + (e.IsStuck ? ", IsStuck" : ""));

        // Entity.OnUpdatePosition: while isUpdatePosition is set, every tick pulls position toward
        // targetPos - MoveTowards, Lerp or a straight snap depending on positionUpdateMovementType.
        // That is the network-interpolation path for REMOTE entities. If it is armed on a local
        // one whose targetPos never advances, the controller can achieve every move it is asked
        // for and the entity still ends each tick exactly where it started - which is what the
        // trace has been showing.
        parts.Add("netpos: " + (e.isUpdatePosition ? "ARMED" : "off")
                  + (e.isEntityRemote ? " REMOTE" : " local")
                  + ", targetPos " + (e.targetPos - e.position).magnitude.ToString("0.00", CultureInfo.InvariantCulture) + " m away");

        var kin = e.m_characterController as CharacterControllerKinematic;
        if (kin != null && kin.motor != null)
        {
            var g = kin.motor.GroundingStatus;
            parts.Add("ground: " + (g.IsStableOnGround ? "stable" : (g.FoundAnyGround ? "UNSTABLE" : "NONE"))
                      + " normal.y " + g.GroundNormal.y.ToString("0.00", CultureInfo.InvariantCulture));
        }

        return "motion: " + string.Join(", ", parts.ToArray());
    }

    static string WhyNotBreaking(EntityAlive e)
    {
        var mh = e.moveHelper;
        if (mh == null) return "break: no move helper";

        var parts = new List<string>();

        parts.Add("blockedTime " + mh.BlockedTime.ToString("0.00", CultureInfo.InvariantCulture)
                  + (mh.BlockedTime >= 0.35f ? " ok" : " TOO SHORT, needs 0.35"));
        parts.Add("canBreak " + (mh.CanBreakBlocks ? "ok" : "NO"));
        parts.Add("jumping " + (e.Jumping ? "YES - blocks it" : "no"));

        var mask = e.crouchType != 0 ? 5 : (e.physicsHeight >= 1f ? 7 : 5);
        parts.Add("flags " + mh.BlockedFlags + " & " + mask
                  + ((mh.BlockedFlags & mask) > 0 ? " ok" : " NOTHING IN THE WAY"));

        var world = WerewolfWorld.World;
        if (world != null)
        {
            var pos = mh.HitInfo.hit.blockPos;
            var block = world.GetBlock(pos);
            parts.Add("hit block " + pos + (block.isair ? " is AIR - blocks it" : " solid ok"));
        }

        // The LAST condition in EAIBreakBlock.CanExecute, and the only one this trace never showed.
        // It wants CalcBlockedDistanceSq() <= (controller radius + 0.7)^2 - so a creature that is
        // flagged as blocked but standing back from what blocked it still gets refused.
        var cc = e.m_characterController;
        if (cc != null)
        {
            var limit = cc.GetRadius() + 0.7f;
            var blockedSq = mh.CalcBlockedDistanceSq();
            // A blocked distance only means anything once something has actually been recorded as
            // blocking. With HitInfo unset the figure is the distance to the world origin - it read
            // 3351 m in the log and looked like a real measurement, which it is not.
            var blocked = Mathf.Sqrt(Mathf.Max(0f, blockedSq));
            parts.Add(blocked > 100f
                ? "blocked dist n/a - nothing recorded as blocking"
                : "blocked dist " + blocked.ToString("0.00", CultureInfo.InvariantCulture)
                  + " m vs limit " + limit.ToString("0.00", CultureInfo.InvariantCulture)
                  + (blockedSq <= limit * limit ? " ok" : " TOO FAR - blocks it"));
        }
        return "break: " + string.Join(", ", parts.ToArray());
    }

    static string Distance(EntityAlive a, Entity b) =>
        (a.position - b.position).magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m";

    // ---------------------------------------------------------------- helpers

    /// <summary>Every werewolf in the world, remote copies included - this is a look-at-it command
    /// and on a client the remote copies are all there is to look at.</summary>
    static List<EntityAlive> Live() => WerewolfWorld.Live(localOnly: false);

    static IEnumerable<EntityClass> Classes()
    {
        foreach (var pair in EntityClass.list.Dict)
        {
            var cls = pair.Value;
            if (cls != null && !string.IsNullOrEmpty(cls.entityClassName)
                && cls.entityClassName.StartsWith(ClassPrefix, StringComparison.Ordinal))
            {
                yield return cls;
            }
        }
    }

    static bool TryFloat(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    static void Out(string message)
    {
        SdtdConsole.Instance.Output(message);
        Log.Out("[Werewolf] " + message);
    }
}
