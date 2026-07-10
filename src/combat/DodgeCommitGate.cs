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

    /// <summary>
    ///     Gate for a slot FIRST resolving a dodge at impact (the p5=0 skip in
    ///     <c>MsSetDamageInternal</c> and the <c>MsDamageSetMotion</c> negation).
    ///
    ///     <see cref="ShouldSkipCommit"/> trusts the durable marker, which is only ever set for a
    ///     targeted slot once this gate has passed. This gate is the point where that marker is
    ///     established, so it must also reject slots the attack never targeted: the engine invokes
    ///     the commit for every party slot, and the window/attacker checks alone are cue-wide, not
    ///     per-slot. <paramref name="armedTargetMask"/> is the snapshot of the cue's targeted slots
    ///     taken when the window was armed, so an untargeted slot cannot ride the shared gate.
    /// </summary>
    /// <param name="dodgeEnabled">The <c>dodgeEnabled</c> setting.</param>
    /// <param name="windowLiveOrMarker">Wall-clock dodge window still live for this cue, OR the
    ///     slot already carries the durable evade marker.</param>
    /// <param name="armedAttackerId">Attacker the dodge window was armed against.</param>
    /// <param name="impactAttackerId">Attacker driving the impact under inspection.</param>
    /// <param name="armedTargetMask">Party slots the window was armed for (bit per slot).</param>
    /// <param name="slotIndex">Party slot under inspection.</param>
    public static bool MayResolveAtImpact(
        bool dodgeEnabled,
        bool windowLiveOrMarker,
        byte armedAttackerId,
        byte impactAttackerId,
        uint armedTargetMask,
        int slotIndex)
        => dodgeEnabled
        && windowLiveOrMarker
        && armedAttackerId == impactAttackerId
        && (armedTargetMask & (1u << slotIndex)) != 0;
}
