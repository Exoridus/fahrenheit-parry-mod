namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private void h_ms_damage_set_motion(byte target, int p2, int p3)
    {
        using var validation = begin_native_validation("motion", -1, target, p2, flags: p3);

        // Motion has no attacker argument; native apply for this target has already passed admission.
        // Its HP/status/death work cannot be undone by the direct-motion HP fallback.
        if (target < PartyActorCapacity && _nativeDamageCommitAttacker[target].HasValue)
        {
            if (p3 == 1) _attackTelemetry[target].SetMotionFired = true;
            orig_ms_damage_set_motion(target, p2, p3);
            if (validation is not null) validation.Completed = true;
            return;
        }

        // Direct motion calls outside staged application retain their existing reactive fallback.
        bool targetIsParty = target < PartyActorCapacity;
        bool parryActive   = targetIsParty
                             && _optionEnabled
                             && is_parry_damage_window_open(_runtime.CurrentAttackerId, target)
                             && p3 == 1;

        // When parry is active but the enemy missed (damage_hp=0), do NOT suppress the flinch —
        // let the native miss/dodge animation play normally. Only suppress when actual damage
        // was pending, meaning we intercepted a real hit.
        bool suppressFlinch = false;
        if (parryActive)
        {
            Chr* flinchParty = _battleAdapter.GetPlayerCharacters();
            Chr* flinchTarget = flinchParty != null ? flinchParty + target : null;
            suppressFlinch = flinchTarget != null && flinchTarget->stat_exist_flag && flinchTarget->damage_hp > 0;
        }

        // Dodge (L1) resolves at impact, symmetric to parry: if the window is still valid for
        // this attacker, this slot was among the armed targets, and real damage is pending, negate
        // it and run the engine's own evade. The target-mask check keeps an untargeted party slot
        // from resolving off the cue-wide window/attacker gate (mirrors DodgeCommitGate at p5=0).
        bool dodgeActive = targetIsParty && p3 == 1
            && DodgeCommitGate.MayResolveAtImpact(
                _optionDodgeEnabled,
                _dodgeWindowActive && _runtime.CueFirstSeenFrame == _dodgeArmedCueFrame,
                _dodgeArmedAttackerId,
                _runtime.CurrentAttackerId,
                _dodgeArmedTargetMask,
                target);
        bool dodgeSuppress = false;
        if (dodgeActive)
        {
            Chr* dodgeParty = _battleAdapter.GetPlayerCharacters();
            Chr* dodgeTarget = dodgeParty != null ? dodgeParty + target : null;
            dodgeSuppress = dodgeTarget != null && dodgeTarget->stat_exist_flag && dodgeTarget->damage_hp > 0;
        }

        bool defended = (dodgeActive && dodgeSuppress) || (parryActive && suppressFlinch);

        if (!defended)
        {
            orig_ms_damage_set_motion(target, p2, p3);
        }
        else
        {
            if (dodgeActive && dodgeSuppress)
            {
                // The step-out movement was triggered on press (handle_dodge_input_press). At
                // impact we only negate the hit and suppress the flinch (skip orig → no hit
                // reaction, no second evade trigger). The resolved-mask makes MsSetDamageInternal
                // skip its authoritative HP/death commit; the shared restore below undoes any
                // HP already applied.
                _dodgeEvadeCount++;
                mark_dodge_resolved(target);
                _dodgeTextRemainingSeconds = ParriedTextSeconds;
                _dodgeTextSeed = next_label_seed();
                _dodgeTextTargetMask |= 1u << target;
                log_debug($"Dodge negated hit for {format_actor_slot(target)} (movement started on press; flinch suppressed, dodge#{_dodgeEvadeCount}).");
            }
            else if (_optionParryNativeBlock && !parry_block_recently_played(target))
            {
                // Native block (A): flag the parrying char as guarding (ChrRam+0x19A) so orig's
                // MsDamageSetMotion plays the block reaction 0x43 itself at the real impact — the
                // same field the engine sets for Sentinel/Defend (FFX.exe.c:830149). Set→orig→
                // restore keeps it confined to this one call so it never persists onto later hits.
                Chr* blockParty = _battleAdapter.GetPlayerCharacters();
                Chr* blockChr = blockParty != null ? blockParty + target : null;
                if (blockChr != null)
                {
                    byte* ramGuard = (byte*)&blockChr->ram + ChrRamGuardReactFlagOffset;
                    byte prevGuard = *ramGuard;
                    *ramGuard = 1;
                    orig_ms_damage_set_motion(target, p2, p3);
                    *ramGuard = prevGuard;
                }
                else
                {
                    orig_ms_damage_set_motion(target, p2, p3);
                }
                if (target < PartyActorCapacity) _parryBlockPlayedFrame[target] = _debugFrameIndex;
                log_debug($"Parry native block for {format_actor_slot(target)} (guard flag → engine plays 0x43).");
            }
            else
            {
                log_debug($"Parry suppressed flinch for {format_actor_slot(target)} (MsDamageSetMotion skipped).");
            }

            // Additive restore: ram.hp += damage_hp undoes whatever the native pipeline
            // already applied. For reactive parries the snapshot is never set (calc fired
            // before R1 press), so we must not gate on snapshot > 0.
            // If the snapshot WAS set (proactive interception), cap to it to prevent
            // over-restore when a lethal hit clamped HP to 0 before our hook fired.
            //
            if (_optionNegateDamage)
            {
                Chr* party = _battleAdapter.GetPlayerCharacters();
                Chr* targetChr = party != null ? party + target : null;
                if (targetChr != null && targetChr->stat_exist_flag && targetChr->damage_hp > 0)
                {
                    bool wasLethal = targetChr->ram.hp <= 0;
                    int restored = (int)targetChr->ram.hp + targetChr->damage_hp;
                    uint snap = _preHitHpSnapshot[target];
                    if (snap > 0 && (uint)restored > snap) restored = (int)snap;
                    targetChr->ram.hp = restored;

                    if (wasLethal && targetChr->ram.hp > 0)
                    {
                        // Clear the same death-latch fields as the h_ms_set_damage path —
                        // see ExternalMemoryOffsetMap.ChrStruct for evidence + offsets.
                        byte* chrB = (byte*)targetChr;
                        chrB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch] = 0;
                        *(ushort*)(chrB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits)
                            &= unchecked((ushort)~ExternalMemoryOffsetMap.ChrStruct.DeadStatusBitMask);
                        targetChr->stat_will_die = 0;

                        log_debug($"Parry lethal restore (motion) for {format_actor_slot(target)}: HP restored to {(uint)targetChr->ram.hp}, death-latch cleared.");
                    }
                    else
                    {
                        log_debug($"Parry restored {targetChr->damage_hp} HP for {format_actor_slot(target)} (hp now={(uint)targetChr->ram.hp}).");
                    }
                }
            }
        }

        // p3=1 signals a damage-involved motion for party targets (confirmed from session log
        // analysis: p2=5 for standard physical attacks, p2=1 for ramming attacks — both with p3=1).
        // p3=0 calls are cosmetic/non-damage motions and must not trigger parry resolution.
        bool isDamageMotion = p3 == 1 && targetIsParty;

        if (isDamageMotion)
            _attackTelemetry[target].SetMotionFired = true;

        if (isDamageMotion)
        {
            bool pendingDeferred = _parryFeedbackPending[target];
            Chr* reactiveCandidate = null;
            bool reactiveParry   = false;
            if (!pendingDeferred
                && _optionEnabled
                && parryActive
                && (_runtime.CurrentPartyTargetMask & (1u << target)) != 0
                && (_runtime.LastParriedTargetMask  & (1u << target)) == 0)
            {
                Chr* rParty = _battleAdapter.GetPlayerCharacters();
                reactiveCandidate = rParty != null ? rParty + target : null;
                // Only resolve reactively if actual damage is pending — damage_hp=0 means
                // the enemy missed/was evaded, not a successful parry. Also skip if the target
                // was already non-parryable (confused, berserk, etc.) before this hit.
                reactiveParry = reactiveCandidate != null
                    && reactiveCandidate->damage_hp > 0
                    && !is_target_non_parryable(reactiveCandidate);
            }

            if (pendingDeferred || reactiveParry)
            {
                // Set the durable impact marker for this slot so that h_ms_set_damage_internal
                // p5=1024 can skip delayed application without re-checking the
                // simulation window. The marker survives window expiry for attacks
                // (Anfunkeln, Blitzra) where the commit pass is delayed past the timing window.
                // Guard: only set once per slot per turn.
                if ((_parryResolvedAtImpactMask & (1u << target)) == 0)
                {
                    _parryResolvedAtImpactMask |= 1u << target;
                }

                Chr* party = _battleAdapter.GetPlayerCharacters();
                Chr* candidate = party != null ? party + target : null;
                if (candidate != null && candidate->stat_exist_flag)
                {
                    string source = pendingDeferred ? "deferred" : "visual";
                    resolve_successful_parry(target, candidate, source, closeWindow: false);
                }
                _parryFeedbackPending[target] = false;
            }
        }

        if (_optionLogging)
        {
            write_session_hook_entry($"[MsDamageSetMotion] f={_debugFrameIndex} target={target} p2={p2} p3={p3} parry={_runtime.ParryWindowActive}");
        }
        if (validation is not null) validation.Completed = true;
    }
}
