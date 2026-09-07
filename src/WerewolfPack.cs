using HarmonyLib;

/// <summary>
/// A werewolf's claws pass through another werewolf.
///
/// THE PROBLEM. Two of them swinging at the same player trade hits with each other, and a howl that
/// lands its summon in the melee starts a brawl inside the pack. The melee ray is a sphere cast
/// down the creature's own facing (ItemActionAttack.Hit), so anything standing between it and the
/// player takes the swing - and with 65 entity damage a pack-mate loses a real bite of health.
/// Worse, EntityAlive.ProcessDamageResponseLocal then hands the hurt one a revenge target, and
/// AITarget lists EntityEnemyAnimal in SetAsTargetIfHurt, so the two of them turn on each other and
/// forget the player entirely. The three colour variants are separate entity classes but all
/// werewolves, so a snow one and a dark one fight just as readily as two of a kind.
///
/// WHY NOT XML. There is no faction switch that would do it. Factions exist and vanilla animals all
/// sit in "animals", but FactionManager.GetRelationshipValue is read in exactly two places -
/// bandit/NPC utility AI and MinEventActionTargetedBase.isValidTarget - and by nothing on the
/// damage path. Two animals of the same faction damage each other perfectly well in vanilla; the
/// game simply never puts two of a pack-hunting animal in a position to mind.
///
/// WHAT THIS DOES. Entity.CanDamageEntity(sourceEntityId) is a virtual whose base is a single
/// `return true`, and ItemActionAttack.Hit calls it on whatever the swing found and returns
/// immediately when it says no. So refusing there costs the swing and nothing else: no damage, no
/// hit effect, no revenge target, no dismemberment roll. The werewolf simply misses.
///
/// It is deliberately narrow - it only refuses when BOTH sides are werewolves. A werewolf still
/// takes full damage from players, zombies, bears and everything else, and still deals it to
/// everything that is not one of its own.
/// </summary>
public static class WerewolfPack
{
    public static bool Enabled = true;

    static int blocked;

    /// <summary>What the trace prints.</summary>
    public static string Describe()
    {
        return "pack: friendly fire " + (Enabled ? "OFF" : "ON")
               + ", " + blocked + " swings between werewolves ignored so far";
    }

    [HarmonyPatch(typeof(Entity), "CanDamageEntity")]
    public static class NotEachOther
    {
        static bool Prefix(Entity __instance, int _sourceEntityId, ref bool __result)
        {
            if (!Enabled) return true;

            // The victim first - it is the cheaper test and the one that fails most often.
            if (!WerewolfWorld.IsWerewolf(__instance)) return true;

            var world = WerewolfWorld.World;
            var attacker = world == null ? null : world.GetEntity(_sourceEntityId);
            if (attacker == null || !WerewolfWorld.IsWerewolf(attacker)) return true;

            // Never let it stop a creature hurting ITSELF, whatever might arrange that - blocking
            // that could silently cancel a future self-damage buff rather than friendly fire.
            if (attacker.entityId == __instance.entityId) return true;

            blocked++;
            __result = false;
            return false;
        }
    }
}
