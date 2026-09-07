/// <summary>
/// The mod's entry point: it hands a per-frame tick to the three things that need one.
///
///   WerewolfSpawnGate   the creature only exists at night and past a game stage
///   WerewolfHowl        the summon, its shared pack limit and its lock-out
///   WerewolfSwipe       which arm the next attack swings with
///   ConsoleCmdWerewolf  the tuning trace, when it is switched on
///
/// The console command needs no registration of its own - the game's console scans loaded
/// assemblies for ConsoleCmdAbstract subclasses and finds it.
///
/// THIS USED TO BE OPTIONAL AND IS NOT ANY MORE. The creature itself is still just an asset bundle
/// and XML - an animal carries its animator controller inside its own prefab, so nothing has to be
/// loaded at runtime - and it will spawn, move, attack and die with src\ deleted. What goes with
/// the DLL is the night gate and the howl: both are behaviour no XML in the game can express, so
/// without this assembly the werewolf is a plain, permanent, solitary animal.
///
/// The cost of shipping it: a mod with a DLL has to set SkipWithAntiCheat in ModInfo.xml, so the
/// game must be launched without EAC (7DaysToDie.exe, not 7DaysToDie_EAC.exe).
/// </summary>
public class WerewolfModApi : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        // WHICH BUILD IS RUNNING, in the log, on the first line. Three test rounds in a row were
        // spent arguing about a fix that was not in the game: the DLL had been redeployed while the
        // session was already up, and a running game never rereads it. The log said nothing about
        // it, so the only clue was comparing file timestamps after the fact. Now it says.
        var built = "unknown";
        try
        {
            var path = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                built = System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
            }
        }
        catch (System.Exception)
        {
            // Never worth failing a mod load over a timestamp.
        }

        Log.Out("[Werewolf] " + _modInstance.Name + " loaded, build of " + built
                + " - type 'werewolf' in the console for tuning commands");
        Log.Out("[Werewolf] if that build time is older than something you just changed, the game"
                + " is running a stale DLL and needs restarting");
        // Harmony patches, each werewolf-only on its first line, so every other entity in the
        // world leaves before anything is read:
        //
        //   WerewolfPress   holds wall contact together across a bounce. EAIBreakBlock waits for
        //                   BlockedTime to reach 0.35, the game only adds to it on frames where
        //                   BlockedFlagsAfterCrouch is set - a field this creature never gets -
        //                   and one clear frame resets it. Measured: blocked in about a fifth of
        //                   ticks, afterCrouch in none of them.
        //   WerewolfAim     points the melee ray at the target instead of straight ahead.
        //                   EntityLookHelper only ever decays an animal's pitch towards zero, so
        //                   without this it cannot hit anything above or below its own eye.
        //
        // Neither can be expressed in XML. An earlier patch on SetMoveTo was removed for being
        // dead weight on a hot vanilla method; these two earn their place.
        new HarmonyLib.Harmony("com.lyovi.werewolf").PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
        ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
    }

    static void OnGameUpdate(ref ModEvents.SGameUpdateData _data)
    {
        // Order matters by a hair: the gate runs first so the howl never rolls for a werewolf that
        // is about to be removed this same frame.
        WerewolfSpawnGate.Tick();
        WerewolfHowl.Tick();
        WerewolfSwipe.Tick();
        WerewolfBrawl.Tick();
        WerewolfEars.Tick();
        ConsoleCmdWerewolf.Tick();
    }
}
