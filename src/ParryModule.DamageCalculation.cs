namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    /// <summary>
    ///     Returns true if the target's current battle status prevents them from being parried.
    ///     KO, Petrification, Sleep, and Confusion are non-parryable: the target cannot react,
    ///     and the player should not be penalised for failing to parry on their behalf.
    /// </summary>
    private static bool has_confuse_status(Chr* target)
    {
        if (target == null || !target->stat_exist_flag) return false;
        return target->ram.status_suffer.HasFlag(StatusPermanentFlags.CONFUSION);
    }

    private static bool is_target_non_parryable(Chr* target)
    {
        if (target == null || !target->stat_exist_flag) return true;
        if (target->stat_death != 0) return true;                        // KO'd
        if (target->stat_stone != 0) return true;                        // Petrified
        if (target->ram.status_suffer_turns_left.sleep > 0) return true; // Sleeping
        if (has_confuse_status(target)) return true;                      // Confused
        if (target->ram.status_suffer.HasFlag(StatusPermanentFlags.BERSERK)) return true; // Berserk
        return false;
    }

    private bool is_parry_damage_window_open(int attacker, int target)
        => is_parry_slot_window_open(target)
            && _parryArmedAttackerId[target] == attacker
            && _runtime.CurrentAttackerId == attacker;

    /// <summary>
    ///     Active hook on MsCalcDamage (community-confirmed 11-param signature, March 2026).
    ///     Fires with per-target info for ALL attack types — physical and magic alike — making
    ///     it the authoritative damage interception point for parry resolution.
    ///
    ///     When a party slot has an active parry tick deadline, the hook skips the native
    ///     MsCalcDamage call entirely and returns 0 — preventing damage from entering the
    ///     native pipeline. This replaces the previous IMMUNITY_HP_DAMAGE flag mechanism.
    ///
    ///     The check MUST happen BEFORE orig is called. If orig runs, damage enters the
    ///     native buffer and cannot be recalled.
    ///
    ///     The MsSetDamage p2=target path remains as a secondary feedback path for physical
    ///     attacks. The Anfunkeln finalization fallback remains as a safety net for attacks
    ///     that bypass MsCalcDamage entirely.
    /// </summary>
    private int h_ms_calc_damage(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
    {
        using var validation = begin_native_validation("calc", user_id, target_id, command_id, batch: p11, target: target_chr);

        // Check the parry tick deadline BEFORE calling orig — skipping orig is how we prevent damage
        // from entering the native buffer. Once orig runs, damage cannot be recalled.
        bool isPartyTarget = target_id >= 0 && target_id < PartyActorCapacity;
        bool shouldIntercept = isPartyTarget
            && _optionEnabled
            && is_parry_damage_window_open(user_id, target_id)
            && (_runtime.LastParriedTargetMask & (1u << target_id)) == 0;

        if (isPartyTarget)
        {
            _attackTelemetry[target_id].CalcDamageFired = true;
            if (_attackTelemetry[target_id].CommandId == 0)
                _attackTelemetry[target_id].CommandId = command_id;
        }

        Chr* targetChrPtr = isPartyTarget && target_chr != 0 ? (Chr*)target_chr : null;

        // Unconditional pre-hit HP snapshot for all party targets — captured before orig runs.
        // Reactive parries (R1 pressed after calc fires) never enter shouldIntercept, so this
        // is the only site that captures snapshot for reactive-parry lethal restore.
        // Guard == 0: preserve first-hit value on multi-hit attacks (don't overwrite with 0).
        if (isPartyTarget && targetChrPtr != null && targetChrPtr->stat_exist_flag
            && _preHitHpSnapshot[target_id] == 0)
        {
            _preHitHpSnapshot[target_id] = (uint)targetChrPtr->ram.hp;
        }

        if (shouldIntercept)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + target_id : null;
            if (candidate != null && candidate->stat_exist_flag && !is_target_non_parryable(candidate))
            {
                _attackTelemetry[target_id].CalcDamageIntercepted = true;
                _runtime.LastParriedTargetMask |= 1u << target_id;
                _parryFeedbackPending[target_id] = true;
                _damageEventActive[target_id] = true;
                log_debug($"MsCalcDamage intercepted: {format_actor_slot((byte)user_id)} -> {format_actor_slot((byte)target_id)} (cmd={command_id}), damage blocked, feedback deferred. HP snapshot={_preHitHpSnapshot[target_id]}");
                log_hook_ms_calc_damage(user_id, target_id, command_id, p11, 0, command);
                if (validation is not null) validation.Completed = true;
                return 0;
            }
        }

        int result = orig_ms_calc_damage(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

        log_hook_ms_calc_damage(user_id, target_id, command_id, p11, result, command);
        if (validation is not null) validation.Completed = true;
        return result;
    }

    private int h_ms_calc_damage_internal(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
    {
        using var validation = begin_native_validation("calc_internal", user_id, target_id, command_id, target: target_chr);

        // Inner interception: check the parry tick deadline BEFORE calling orig.
        // This is the safety net for attacks where the outer h_ms_calc_damage interception
        // fails because LastParriedTargetMask was already set by MsDamageSetMotion.
        // Do NOT check LastParriedTargetMask here — that is the entire reason this inner
        // hook exists. The outer already checks it; when outer passes through, inner must
        // still fire based on expiry alone.
        bool isPartyTarget = target_id >= 0 && target_id < PartyActorCapacity;
        bool innerShouldIntercept = isPartyTarget
            && _optionEnabled
            && is_parry_damage_window_open(user_id, target_id)
            && !is_target_non_parryable((Chr*)target_chr);

        if (innerShouldIntercept)
        {
            // Snapshot HP if not already captured by the outer h_ms_calc_damage hook.
            if (_preHitHpSnapshot[target_id] == 0 && target_chr != nint.Zero)
            {
                Chr* snapTarget = (Chr*)target_chr;
                if (snapTarget->stat_exist_flag)
                    _preHitHpSnapshot[target_id] = (uint)snapTarget->ram.hp;
            }
            _parryFeedbackPending[target_id] = true;
            _runtime.LastParriedTargetMask |= 1u << target_id;
            _damageEventActive[target_id] = true;
            write_session_hook_entry(
                $"[DmgCalcInternal] INTERCEPTED frame={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4}");
            if (validation is not null) validation.Completed = true;
            return 0;
        }

        int damageHpBefore = 0;
        int ramHpBefore = 0;
        bool readOk = false;

        if (_optionLogging && target_chr != nint.Zero)
        {
            try
            {
                Chr* tgt = (Chr*)target_chr;
                damageHpBefore = tgt->damage_hp;
                ramHpBefore = tgt->ram.hp;
                readOk = true;
            }
            catch
            {
                // target_chr may not be a valid Chr* — swallow and log what we can
            }
        }

        int result = orig_ms_calc_damage_internal(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

        if (_optionLogging)
        {
            int damageHpAfter = 0;
            int ramHpAfter = 0;

            if (readOk && target_chr != nint.Zero)
            {
                try
                {
                    Chr* tgt = (Chr*)target_chr;
                    damageHpAfter = tgt->damage_hp;
                    ramHpAfter = tgt->ram.hp;
                }
                catch
                {
                    readOk = false;
                }
            }

            if (readOk)
            {
                write_session_hook_entry(
                    $"[DmgCalcInternal] f={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4} result={result}" +
                    $" ramHp_before={ramHpBefore} ramHp_after={ramHpAfter}" +
                    $" damageHp_before={damageHpBefore} damageHp_after={damageHpAfter}" +
                    $" raw_arg11=0x{(uint)p11:X8}");
            }
            else
            {
                write_session_hook_entry(
                    $"[DmgCalcInternal] f={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4} result={result}" +
                    $" (target_chr read failed) raw_arg11=0x{(uint)p11:X8}");
            }
        }

        if (validation is not null) validation.Completed = true;

        return result;
    }
}
