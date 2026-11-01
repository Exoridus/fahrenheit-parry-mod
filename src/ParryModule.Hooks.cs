namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private uint h_ms_exe_input_cue(uint actorId, byte mode)
    {
        bool hadBefore = try_get_head_cue_snapshot(_debugHookCueScratch, out DebugCueSnapshot before);
        ulong frame = _debugFrameIndex;
        DateTime now = current_gameplay_timestamp();

        uint result = orig_ms_exe_input_cue(actorId, mode);

        bool hasAfter = try_get_head_cue_snapshot(_debugHookCueScratch, out DebugCueSnapshot after);
        bool changed = !hadBefore || !hasAfter || !before.EqualsSemantic(after);

        if (hadBefore && changed)
        {
            _runtime.LastDispatchConsumedFrame = frame;
            _runtime.LastDispatchConsumedAttackerId = before.AttackerId;
            _runtime.LastDispatchConsumedQueueIndex = before.QueueIndex;

            _turnRuntimeEvents.EmitDispatchConsumed(
                attackerId: before.AttackerId,
                queueIndex: before.QueueIndex,
                timestampLocal: now,
                frameIndex: frame,
                reason: "native dispatch");
        }

        if (hasAfter && changed)
        {
            _turnRuntimeEvents.EmitDispatchStarted(
                attackerId: after.AttackerId,
                queueIndex: after.QueueIndex,
                timestampLocal: now,
                frameIndex: frame,
                parryWindowActive: _runtime.ParryWindowActive);
        }
        return result;
    }

    /// <summary>
    ///     Writes MsSetDamage hook telemetry to the session log file only (not the overlay).
    ///     High-frequency hook data is useful for post-session analysis but clutters the
    ///     real-time debug overlay with low-signal noise.
    /// </summary>
    private void log_hook_ms_set_damage(byte param_1, int param_2, int param_3, int result)
    {
        if (!_optionLogging)
            return;

        ulong frame = _debugFrameIndex;
        bool parryWindowActive = _runtime.ParryWindowActive;
        byte currentAttackerId = _runtime.CurrentAttackerId;
        bool awaitingTurnEnd = _runtime.AwaitingTurnEnd;
        bool sameAsLast =
            _msSetDamageLogLastFrame != 0
            && _msSetDamageLogLastP1 == param_1
            && _msSetDamageLogLastP2 == param_2
            && _msSetDamageLogLastP3 == param_3
            && _msSetDamageLogLastResult == result
            && _msSetDamageLogLastParryWindowActive == parryWindowActive
            && _msSetDamageLogLastAttackerId == currentAttackerId
            && _msSetDamageLogLastAwaitingTurnEnd == awaitingTurnEnd;

        if (sameAsLast && frame - _msSetDamageLogLastFrame < 30)
            return;

        _msSetDamageLogLastFrame = frame;
        _msSetDamageLogLastP1 = param_1;
        _msSetDamageLogLastP2 = param_2;
        _msSetDamageLogLastP3 = param_3;
        _msSetDamageLogLastResult = result;
        _msSetDamageLogLastParryWindowActive = parryWindowActive;
        _msSetDamageLogLastAttackerId = currentAttackerId;
        _msSetDamageLogLastAwaitingTurnEnd = awaitingTurnEnd;

        // Route to session file log only — not to overlay ring buffer.
        write_session_hook_entry($"[MsSetDamage] f={frame} p1={param_1} p2={param_2} p3={param_3} ret={result} parry={parryWindowActive} atk={currentAttackerId} await={awaitingTurnEnd}");
    }

    private void log_hook_ms_calc_damage(int user_id, int target_id, int command_id, int p11, int result, nint command)
    {
        if (!_optionLogging)
            return;

        ulong frame = _debugFrameIndex;
        bool sameAsLast =
            _msCalcDamageLogLastFrame != 0
            && _msCalcDamageLogLastUserId == user_id
            && _msCalcDamageLogLastTargetId == target_id
            && _msCalcDamageLogLastCommandId == command_id
            && _msCalcDamageLogLastHitCount == p11
            && _msCalcDamageLogLastResult == result;

        if (sameAsLast && frame - _msCalcDamageLogLastFrame < 30)
            return;

        _msCalcDamageLogLastFrame = frame;
        _msCalcDamageLogLastUserId = user_id;
        _msCalcDamageLogLastTargetId = target_id;
        _msCalcDamageLogLastCommandId = command_id;
        _msCalcDamageLogLastHitCount = p11;
        _msCalcDamageLogLastResult = result;

        // Route to session file log only — high-frequency hook data clutters the overlay.
        write_session_hook_entry($"[MsCalcDamage] f={frame} user={user_id} target={target_id} cmd={command_id} hits={p11} ret={result} cmd_ptr=0x{command:X}");
    }

    private int h_dmg_calc_armored(Chr* user, Chr* target, Command* command, int p4, int* p5, int damage)
    {
        int result = orig_dmg_calc_armored(user, target, command, p4, p5, damage);

        if (_optionLogging)
        {
            write_session_hook_entry($"[DmgCalcArmored] f={_debugFrameIndex} damage_in={damage} damage_out={result} parry={_runtime.ParryWindowActive}");
        }

        return result;
    }

    private bool try_get_head_cue_snapshot(List<DebugCueSnapshot> scratch, out DebugCueSnapshot head)
    {
        scratch.Clear();
        collect_live_cues(scratch, out _);
        if (scratch.Count == 0)
        {
            head = default;
            return false;
        }

        head = scratch[0];
        return true;
    }

    /// <summary>
    ///     Active hook on MsAtelRequestCamera (FFX.exe+0x397BD0).
    ///
    ///     The function is the central gate for in-game camera change requests
    ///     (called from 12 sites — battle camera setup, scene transitions, etc.).
    ///     We intercept and short-circuit the call when both:
    ///       - the user has the enemy-turn camera lock enabled (default-on);
    ///       - and an enemy turn is currently in progress (cue active, attacker id
    ///         outside party slot range 0..PartyActorCapacity-1).
    ///
    ///     Result: during enemy turns the camera stays at whatever position the
    ///     game left it in for the player, keeping incoming attack animations
    ///     readable for parry timing. Player turns and out-of-battle camera
    ///     changes pass through unchanged.
    ///
    ///     Return value at all 12 observed call sites is unused, so returning 0
    ///     on suppression is safe.
    /// </summary>
    // Debug camera probe: logs a camera hook invocation with the full lock-gating state, whether
    // or not it was suppressed. Reveals un-locked enemy camera pans (which path fired + why the
    // lock did not engage). Gated on the "camera_probe" setting.
    private void probe_camera_call(string fn, string args, bool anyTurn, bool enemyTurn, bool suppress)
    {
        if (!_optionCameraProbe) return;
        log_debug($"[CameraProbe] {fn}({args}) turn_active={anyTurn} enemy_turn={enemyTurn} attacker={_runtime.CurrentAttackerId} suppress={suppress}");
    }

    // Debug writer probe: logs the ATEL opcodes that actually move the battle camera.
    // Distinct from probe_camera_call, which covers the *request* path (MsAtelRequestCamera
    // and siblings) — those only enqueue camera scripts and never touch camera state, which
    // is why an enemy pan runs straight past all three of them. Gated on "camera_probe".
    private void probe_camera_writer(string fn, string args)
    {
        if (!_optionCameraProbe) return;

        bool anyTurn   = _runtime.AwaitingTurnEnd;
        bool enemyTurn = anyTurn && _runtime.CurrentAttackerId >= PartyActorCapacity;
        log_debug($"[CameraWriter] {fn}({args}) turn_active={anyTurn} enemy_turn={enemyTurn} attacker={_runtime.CurrentAttackerId}");
    }

    // The two wrapper constants FUN_007bad30 receives identify the calling opcode exactly.
    private static string polar_opcode_name(int isCam, int variant) => (isCam, variant) switch
    {
        (1, 1) => "camSetBtlPolar",
        (0, 1) => "refSetBtlPolar",
        (1, 2) => "camSetBtlPolar2",
        (0, 2) => "refSetBtlPolar2",
        (1, 3) => "camSetChrPolar",
        (1, 4) => "camSetChrPolar2",
        _      => $"unknownPolar(isCam={isCam},variant={variant})",
    };

    /// <summary>
    ///     True while the battle camera lock should hold the camera hard — the AllTurns mode. In
    ///     that mode, suppressing camera *requests* is not enough: the request lock only fires while
    ///     a turn is active (AwaitingTurnEnd), and the ATEL position writers that pan the camera on
    ///     the player's own turn run with AwaitingTurnEnd false, so they slip past it. So AllTurns
    ///     also stops those writers directly. EnemyTurnsOnly leaves them alone — the writer probe
    ///     shows they never fire during an enemy action, so the request lock covers that mode.
    /// </summary>
    private bool camera_hard_lock_engaged() => should_hold_camera_pose();

    // Whether to suppress a camera writer this call. Always tries to cache the battle camera id (the
    // freecam needs it). Suppresses when the hard lock is engaged — except while the freecam is on
    // but the id is still unresolved, when the write is let through so the game resolves the id.
    // True while we should hold the battle camera still (AllTurns). Immediately when we have a pose to
    // hold (freecam or a saved start camera); otherwise only after the settle grace, so the game frames
    // the battle at its correct default first. This drives BOTH the writer suppression and the request
    // suppression: with requests held too, an ability's camera script — which also drives the
    // zoom/depth writers we do not hook — never queues, so the camera cannot zoom out from under us.
    private bool should_hold_camera() => should_hold_camera_pose();

    private bool should_suppress_camera_writer(int worker, string source, string detail)
    {
        capture_battle_camera_id(worker);
        if (_battleCameraId == 0) return false;   // let the game's write through so the camera id resolves

        // A writer sees the issuing worker but not the request's followed actor, so it cannot be a
        // flow camera as far as this call can tell: -1 says "no actor carried".
        return !camera_move_allowed(source, worker, followedActor: -1, detail: detail)
            && should_hold_camera_pose();
    }

    /// <summary>
    ///     Replicate a suppressed camera writer's ATEL stack pops without applying the write, so the
    ///     VM stack stays balanced — an unbalanced pop desyncs it. Each value must be popped with the
    ///     same primitive the callee used (the pop functions check a per-value type byte), in the
    ///     same order: floats first, then ints. `size` is at offset 0 of AtelStack, so the one
    ///     `stack` pointer serves both pops. Verified against FUN_007bad30 (4 float + 2 int) and
    ///     FUN_007bb620 (3 float).
    /// </summary>
    private void drain_camera_writer_stack(int worker, int stack, int floats, int ints)
    {
        _popStackFloat   ??= get_fptr<AtelPopStackFloatFn>(ExternalMemoryOffsetMap.Functions.AtelPopStackFloat);
        _popStackInteger ??= get_fptr<AtelPopStackIntegerFn>(ExternalMemoryOffsetMap.Functions.AtelPopStackInteger);
        for (int i = 0; i < floats; i++) _popStackFloat(worker, stack);
        for (int i = 0; i < ints; i++)   _popStackInteger(worker, stack);
    }

    /// <summary>
    ///     Hook on FUN_007bad30 (FFX.exe+0x3BAD30) — the actor-relative polar camera writer shared by
    ///     all six camSetBtlPolar/refSetBtlPolar/camSetChrPolar variants. Observe-only unless the
    ///     hard lock is engaged, in which case the write is suppressed: the callee's stack pops
    ///     (4x float, then 2x int) are replicated so the ATEL stack stays balanced, and the original
    ///     — which is what actually moves the camera — is skipped.
    /// </summary>
    private int h_atel_camera_polar_set(int worker, int p2, int stack, int isCam, int variant)
    {
        string name = polar_opcode_name(isCam, variant);
        probe_camera_writer(name, $"isCam={isCam},variant={variant}");
        if (should_suppress_camera_writer(worker, name, $"isCam={isCam},variant={variant}"))
        {
            drain_camera_writer_stack(worker, stack, floats: 4, ints: 2);
            _cameraWriterSuppressCount++;
            return 0;
        }
        return orig_atel_camera_polar_set(worker, p2, stack, isCam, variant);
    }

    /// <summary>
    ///     Hook on FUN_007bb620 (FFX.exe+0x3BB620) — the absolute-position camera writer behind
    ///     camSetPos, the dominant writer outside enemy turns. Observe-only unless the hard lock is
    ///     engaged, in which case the write is suppressed: the callee pops 3 floats off the ATEL
    ///     stack (no ints), replicated here, and the original is skipped.
    /// </summary>
    private void h_atel_camera_pos_set(int worker, int p2, int stack, int p4)
    {
        probe_camera_writer("camSetPos", $"p4={p4}");
        if (should_suppress_camera_writer(worker, "camSetPos", $"p4={p4}"))
        {
            drain_camera_writer_stack(worker, stack, floats: 3, ints: 0);
            _cameraWriterSuppressCount++;
            return;
        }
        orig_atel_camera_pos_set(worker, p2, stack, p4);
    }

    private int h_ms_atel_request_camera(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8)
    {
        // p2 is the actor the camera script follows; 0xFF is the engine's "no actor" sentinel and
        // marks the battle state machine's own framing. One decision, one log line - see
        // camera_move_allowed.
        bool shouldSuppress = !camera_move_allowed(
            "MsAtelRequestCamera", worker: 0, followedActor: p2,
            detail: $"p1={p1:X},p3={p3:X},p4={p4:X}");

        if (shouldSuppress)
        {
            _enemyCameraLockSuppressCount++;
            if (_optionLogging)
            {
                log_debug(
                    $"[CameraLock] Suppressed MsAtelRequestCamera(p1={p1:X}, p2={p2:X}, p3={p3:X}) "
                    + $"(attacker={_runtime.CurrentAttackerId}, count={_enemyCameraLockSuppressCount}).");
            }

            // -1, not 0. MsAtelRequestCamera returns the id of the request it queues via
            // MsAtelRequestExe, and the engine's OWN suppression path — the guard on btl.debug._6_1_
            // (FFX.exe.c:841575) — falls straight through to `return 0xffffffff` (:841607). So -1 is
            // the sanctioned "I queued nothing" value that every caller already handles.
            //
            // 0 is a VALID request id. Returning it told callers a request existed, so a script that
            // waits on its scripted camera waited on a request that was never queued: a hang, not a
            // crash — exactly the freeze seen when an enemy starts a special move, broken only by Esc.
            //
            // To abort a request that HAS been queued, the engine provides MsAtelRequestCancel
            // (FFX.exe.c:841612): AtelSkipReqLevel2 + AtelExecReturn2, then
            // AtelDecodeSignal(req, 0xffffffff), which releases whoever waits on the signal.
            return -1;
        }

        return orig_ms_atel_request_camera(p1, p2, p3, p4, p5, p6, p7, p8);
    }

    /// <summary>
    ///     Active hook on MsAtelRequestMagicCamera (FFX.exe+0x398010). Sibling of
    ///     the MsAtelRequestCamera lock — same gating logic, separate camera path.
    ///     Magic spell camera changes (enemy spells like Fire/Thunder/Demi) route
    ///     through this function instead of MsAtelRequestCamera, so a parallel
    ///     hook is needed for the enemy-turn camera lock to cover both paths.
    ///
    ///     On suppression returns 0xFF — the engine's "no camera assigned"
    ///     sentinel (engine line 841782 default), which is the safe no-op value
    ///     for callers that store the returned camera-id.
    /// </summary>
    private byte h_ms_atel_request_magic_camera(int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9)
    {
        bool shouldSuppress = !camera_move_allowed(
            "MsAtelRequestMagicCamera", worker: 0, followedActor: p2,
            detail: $"p1={p1:X},p3={p3:X}");

        if (shouldSuppress)
        {
            _enemyMagicCameraLockSuppressCount++;
            if (_optionLogging)
            {
                log_debug(
                    $"[CameraLock] Suppressed MsAtelRequestMagicCamera(p1={p1:X}, p2={p2:X}, p3={p3:X}) "
                    + $"(attacker={_runtime.CurrentAttackerId}, count={_enemyMagicCameraLockSuppressCount}).");
            }
            return 0xFF;
        }

        return orig_ms_atel_request_magic_camera(p1, p2, p3, p4, p5, p6, p7, p8, p9);
    }

    /// <summary>
    ///     Active hook on MsBattleSpecialCameraPause (FFX.exe+0x39DDD0). The third
    ///     sibling of the Battle Camera Lock — covers the cinematic camera path
    ///     used by boss / overdrive-class enemy attacks (high-cue 0x40XX commands)
    ///     that bypass MsAtelRequestCamera and MsAtelRequestMagicCamera entirely.
    ///
    ///     Suppression is a bare <c>return</c> (no call to original). Engine-level
    ///     safety: btl._24_1_ stays 0, so the partner MsBattleSpecialCameraFree
    ///     becomes a no-op via its own early guard. No soft-lock risk.
    /// </summary>
    private void h_ms_battle_special_camera_pause(byte mode)
    {
        // No followed actor in the signature, so this cannot be classified as a flow camera here.
        bool shouldSuppress = !camera_move_allowed(
            "MsBattleSpecialCameraPause", worker: 0, followedActor: -1,
            detail: $"mode=0x{mode:X2}");

        if (shouldSuppress)
        {
            _battleSpecialCameraLockSuppressCount++;
            if (_optionLogging)
            {
                log_debug(
                    $"[CameraLock] Suppressed MsBattleSpecialCameraPause(mode=0x{mode:X2}) "
                    + $"(attacker={_runtime.CurrentAttackerId}, count={_battleSpecialCameraLockSuppressCount}).");
            }
            return;
        }

        orig_ms_battle_special_camera_pause(mode);
    }

    /// <summary>
    ///     Observe-only hook on MsEffectEndMotion (FFX.exe+0x387A10) — the engine's
    ///     "a battler's motion just finished" handler. Always calls the original, then
    ///     (when logging is on) logs how long ago we played a motion on that slot via
    ///     the FX/Motion lab or the parry block reaction. This measures the real motion
    ///     run length so we can decide whether to drive the parry window / whiff recovery
    ///     from the animation instead of the static FINAL_PARRY_SPEC durations.
    /// </summary>
    private void h_ms_effect_end_motion(uint chr_id, int mode)
    {
        orig_ms_effect_end_motion(chr_id, mode);

        if (!_optionLogging) return;
        uint slot = chr_id & 0xff;
        if (slot >= PartyActorCapacity) return;
        ulong startFrame = _motionPlayFrame[slot];
        if (startFrame == 0) return; // only report motions we played, so the log stays correlated
        _motionPlayFrame[slot] = 0;
        ulong frames = _debugFrameIndex - startFrame;
        double ms = frames * (1000.0 / BattleFrameRate);
        log_debug($"[MotionEnd] {format_actor_slot((byte)slot)} motion ended after {frames} frames (~{ms:F0} ms @ {BattleFrameRate:F0}fps, mode={mode}).");
    }

    /// <summary>
    ///     Active hook on MsDmgCalc_CheckHit (FFX.exe+0x38A950). The engine's
    ///     accuracy/evasion roll. Always invokes the original to preserve battle
    ///     RNG state, then conditionally overrides MISS → HIT when:
    ///       - the option is enabled,
    ///       - the target is a real PC (not monster, not aeon),
    ///       - and we've auto-cached the HIT enum integer value.
    ///
    ///     The hook also runs auto-discovery for the CheckHitResult enum integers
    ///     by observing returns. Logs every PC-target invocation when logging is on
    ///     so the user can manually verify HIT/MISS values from the session log.
    /// </summary>
    /// <summary>
    ///     MISS_ALIVE is returned by exactly one early-return in the engine
    ///     (`_DmgCalc_CheckHit`, decomp :830187): the command is revive-class
    ///     (`flags_misc` bit 23) and the target is neither zombie nor dead — a Phoenix
    ///     Down or Life aimed at a living character. Its caller (:833532) then `goto`s
    ///     past the entire damage and status chain.
    ///
    ///     It is therefore **not** an evade, and must never be flipped to HIT: doing so
    ///     would land a revive on a healthy party member. It has nothing to do with
    ///     status resistance either — petrify and friends are decided later, in
    ///     `_DmgCalc_InflictStatus` against `status_resists`.
    /// </summary>
    private const uint CommandFlagsMiscReviveClass = 0x800000;

    private int h_ms_dmg_calc_check_hit(Chr* user, Chr* target, Command* command, void* info, int counter)
    {
        int result = orig_ms_dmg_calc_check_hit(user, target, command, info, counter);

        if (target == null) return result;  // defensive — never observed but cheap

        // Chr has TWO id fields:
        //   - id     at 0xC: engine slot index (0..0x1B), what MsGetRamChrMonster operates on.
        //   - chr_id at 0xE: character/monster *template* id (e.g. 0x10CF = Cactuar).
        // Slot is what gates monster/PC discrimination; template is informational only.
        ushort targetSlot = target->id;
        ushort targetTemplate = target->chr_id;
        bool isMonster = (uint)(targetSlot - 0x14u) < 8u;   // matches MsGetRamChrMonster
        bool isAeon = target->ram.is_aeon;
        bool isRealPC = !isMonster && !isAeon;

        if (!isRealPC) return result;  // monsters & aeons keep vanilla evade

        _checkHitObservationCount++;
        if (_optionLogging)
        {
            ushort userSlot = user != null ? user->id : (ushort)0;
            ushort userTemplate = user != null ? user->chr_id : (ushort)0;
            log_debug($"[CheckHit] user_slot={userSlot:X2} user_tpl={userTemplate:X4} target_slot={targetSlot:X2} target_tpl={targetTemplate:X4} is_aeon={isAeon} result={result} (obs#{_checkHitObservationCount}, hit={CheckHitHit})");
        }

        // Native PC evasion stays disabled: a PC that evades natively never reaches our impact
        // path, so the player's parry/dodge is the only way to avoid a hit.
        if (result == CheckHitHit) return result;

        // Do NOT treat every non-HIT value as an evasion roll. The enum has three members, and
        // MISS_ALIVE is identifiable structurally rather than by value: it is returned iff the
        // command is revive-class. Everything else that is not HIT is a genuine evasion roll.
        //
        // This used to be gated on a value discovered by observation, which cached whichever
        // non-HIT value it saw first - on one profile MISS_ALIVE (2), after which the real MISS (1)
        // was never overridden and PCs evaded on their own.
        if (command != null && (command->flags_misc & CommandFlagsMiscReviveClass) != 0) return result;

        _checkHitOverrideCount++;
        if (_optionLogging)
        {
            log_debug($"[CheckHit] Overrode MISS ({result}) → HIT for PC target_slot={targetSlot:X2} target_tpl={targetTemplate:X4} (override#{_checkHitOverrideCount}).");
        }
        return CheckHitHit;
    }

}
