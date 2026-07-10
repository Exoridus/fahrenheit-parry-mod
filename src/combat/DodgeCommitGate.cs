namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Pure gate for the dodge-driven commit skip in <c>MsSetDamageInternal</c>.
///
///     A slot that evaded carries a durable marker for the rest of the cue, so that a
///     multi-hit or AoE swing from the SAME attacker stays fully evaded across its
///     later hits. The marker must never outlive the cue, and it must never apply to a
///     different attacker — otherwise the character becomes immune.
///
///     Kept pure and free of side effects so it can be unit-tested without
///     <c>ParryModule</c>, which is FFX-coupled through its hook installs.
/// </summary>
public static class DodgeCommitGate
{
    /// <param name="dodgeEnabled">The <c>dodgeEnabled</c> setting.</param>
    /// <param name="markerSet">The slot's bit in the durable evade marker.</param>
    /// <param name="armedAttackerId">Attacker the dodge window was armed against.</param>
    /// <param name="commitAttackerId">Attacker driving the commit under inspection.</param>
    public static bool ShouldSkipCommit(
        bool dodgeEnabled,
        bool markerSet,
        byte armedAttackerId,
        byte commitAttackerId)
        => dodgeEnabled
        && markerSet
        && armedAttackerId == commitAttackerId;
}
