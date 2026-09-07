using UnityEngine;

/// <summary>
/// Decides which arm the werewolf swings with.
///
/// The asset has two attack clips, a left swipe and a right one, and the controller picks between
/// them on an int parameter called WerewolfSwipe: 0 left, 1 right. This sets it.
///
/// WHY IT IS HERE AND NOT IN THE ANIMATOR. Nothing the game writes will serve as the coin. It has a
/// Random parameter, but AvatarAnimalController sets it only in StartAnimationHit and
/// StartDeathAnimation - on damage taken and on death, never on an attack. An animator cannot
/// randomise for itself without a StateMachineBehaviour, and a behaviour script inside a mod bundle
/// is a reference the game has no assembly for.
///
/// The version before this split the two clips by SPEED, the way vanilla splits its one attack clip
/// into a standing and a moving variant. That looked reasonable and was wrong: a werewolf stops to
/// swing, so Forward is essentially always 0 when the trigger fires, and the right-hand swipe was
/// never once seen in play. Two animations, one of them dead.
///
/// The value is only ever changed while the creature is NOT mid-attack, so a swing can never switch
/// arms half way through.
///
/// ON A DEDICATED SERVER this runs on the server, like everything else here, and the parameter is
/// local to the machine that sets it - so remote clients would see every swing as a left one. The
/// same limitation as the howl animation, and for the same reason: the game replicates entities,
/// not arbitrary animator parameters. Host and single player are correct.
/// </summary>
public static class WerewolfSwipe
{
    /// <summary>Matches the parameter WerewolfAnimator.cs adds to the controller.</summary>
    public const string Parameter = "WerewolfSwipe";

    /// <summary>How often a new arm is drawn, in seconds. Anything well under the gap between
    /// swings does; this only has to be fresh by the time the next attack starts.</summary>
    const float RollSeconds = 0.5f;

    static readonly System.Random Random = new System.Random();
    static int parameterHash;
    static float nextRoll;

    public static void Tick()
    {
        if (Time.time < nextRoll) return;
        nextRoll = Time.time + RollSeconds;

        if (parameterHash == 0) parameterHash = Animator.StringToHash(Parameter);

        foreach (var e in WerewolfWorld.Live())
        {
            var animator = WerewolfWorld.ModelAnimator(e);
            if (animator == null || animator.runtimeAnimatorController == null) continue;

            // Never mid-swing: changing it now would flip the arm half way through the clip.
            if (animator.GetCurrentAnimatorStateInfo(0).IsTag("Attack")) continue;

            // Drawn per creature rather than from one shared coin, so a pack does not swing in
            // unison.
            animator.SetInteger(parameterHash, Random.Next(0, 2));
        }
    }
}
