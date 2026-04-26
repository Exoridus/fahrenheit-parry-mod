namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    // ── Stage-1 native observe probes ────────────────────────────────────────
    //
    // Log-only hooks for the seven entry points listed in the KB probe plan
    // (docs/fahrenheit-parry-probe-plan.md in ffx-knowledge-base):
    //
    //   MsActionRequest, MsCalcCommand, MsCheckStatusBeforeAction,
    //   MsLimitTypeDamageCheck, MsAtelRequestMagicCamera,
    //   op_et_battle_genko_counter_get, MsSetMotion
    //
    // Each hook body MUST: call orig exactly once, preserve its return value,
    // not mutate args, and not touch parry runtime state. Output is gated by
    // _optionNativeProbeLogging — when the option is OFF (the default), the
    // probe handles are not even hooked in init(), so probe-disabled play is
    // identical to a build without this file.
    //
    // Signatures come from the upstream Fahrenheit __addr_* table where one
    // exists; the actual argument shapes/types come from the engine Ghidra
    // export (.workspace/intermediate/ghidra-server/ffx-v3/functions.tsv).
    // Three of the seven (MsActionRequest, MsLimitTypeDamageCheck, MsSetMotion)
    // resolve to __stdcall in Ghidra and are declared as such here. The other
    // four are declared cdecl/0-arg per Ghidra's "unknown" convention; if a
    // future signature refinement says otherwise, this file is the single
    // place to update.

    /// <summary>
    ///     Per-probe per-frame ceiling. With seven probes this is a worst-case
    ///     56 entries per frame against a 4096-entry ring — comfortably under
    ///     the ~3-frame budget called out in <see cref="NativeProbeRing"/>.
    /// </summary>
    private const int Stage1ProbeMaxPerFrame = 8;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint MsActionRequestProbeDelegate(
        int target_id, int attacker_id, int p3, int p4, int p5, int p6);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsCalcCommandProbeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsCheckStatusBeforeActionProbeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MsLimitTypeDamageCheckProbeDelegate(
        int attacker_id, nint attacker, int target_id, nint target, int p5, int p6, int p7);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsAtelRequestMagicCameraProbeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OpEtBattleGenkoCounterGetProbeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint MsSetMotionProbeDelegate(
        int p1, int p2, int chr_id, byte p4, int p5, int p6, int p7);

    private FhMethodHandle<MsActionRequestProbeDelegate>?           _hStage1MsActionRequest;
    private FhMethodHandle<MsCalcCommandProbeDelegate>?             _hStage1MsCalcCommand;
    private FhMethodHandle<MsCheckStatusBeforeActionProbeDelegate>? _hStage1MsCheckStatusBeforeAction;
    private FhMethodHandle<MsLimitTypeDamageCheckProbeDelegate>?    _hStage1MsLimitTypeDamageCheck;
    private FhMethodHandle<MsAtelRequestMagicCameraProbeDelegate>?  _hStage1MsAtelRequestMagicCamera;
    private FhMethodHandle<OpEtBattleGenkoCounterGetProbeDelegate>? _hStage1OpEtBattleGenkoCounterGet;
    private FhMethodHandle<MsSetMotionProbeDelegate>?               _hStage1MsSetMotion;

    private PerFrameProbeThrottle _throttleMsActionRequest;
    private PerFrameProbeThrottle _throttleMsCalcCommand;
    private PerFrameProbeThrottle _throttleMsCheckStatusBeforeAction;
    private PerFrameProbeThrottle _throttleMsLimitTypeDamageCheck;
    private PerFrameProbeThrottle _throttleMsAtelRequestMagicCamera;
    private PerFrameProbeThrottle _throttleOpEtBattleGenkoCounterGet;
    private PerFrameProbeThrottle _throttleMsSetMotion;

    private bool _stage1ProbesInstalled;

    /// <summary>
    ///     Lazily install the Stage-1 probe handles when the user has opted
    ///     in to native probe logging. Skipped entirely when the option is
    ///     off, so default play installs zero extra hooks.
    /// </summary>
    /// <remarks>
    ///     Must be called from <c>init()</c> AFTER <c>load_persistent_settings()</c>
    ///     so <c>_optionNativeProbeLogging</c> reflects on-disk state.
    ///     Each install is wrapped in its own try/catch and a failure for one
    ///     probe must not block the others — matches the convention used for
    ///     existing damage hooks.
    /// </remarks>
    private void install_stage1_probes()
    {
        if (_stage1ProbesInstalled) return;
        if (!_optionNativeProbeLogging)
        {
            _logger.Info("[Parry] Stage-1 native probes inert (NativeProbeLogging=false).");
            return;
        }

        _hStage1MsActionRequest = new FhMethodHandle<MsActionRequestProbeDelegate>(
            this, "FFX.exe", FhFfx.FhCall.__addr_MsActionRequest, h_stage1_ms_action_request);
        _hStage1MsCalcCommand = new FhMethodHandle<MsCalcCommandProbeDelegate>(
            this, "FFX.exe", FhFfx.FhCall.__addr_MsCalcCommand, h_stage1_ms_calc_command);
        _hStage1MsCheckStatusBeforeAction = new FhMethodHandle<MsCheckStatusBeforeActionProbeDelegate>(
            this, "FFX.exe", FhFfx.FhCall.__addr_MsCheckStatusBeforeAction, h_stage1_ms_check_status_before_action);
        _hStage1MsLimitTypeDamageCheck = new FhMethodHandle<MsLimitTypeDamageCheckProbeDelegate>(
            this, "FFX.exe", FhFfx.FhCall.__addr_MsLimitTypeDamageCheck, h_stage1_ms_limit_type_damage_check);
        _hStage1MsAtelRequestMagicCamera = new FhMethodHandle<MsAtelRequestMagicCameraProbeDelegate>(
            this, "FFX.exe", FhFfx.FhCall.__addr_MsAtelRequestMagicCamera, h_stage1_ms_atel_request_magic_camera);
        _hStage1OpEtBattleGenkoCounterGet = new FhMethodHandle<OpEtBattleGenkoCounterGetProbeDelegate>(
            this, "FFX.exe", FhFfx.FhCall.__addr_op_et_battle_genko_counter_get, h_stage1_op_et_battle_genko_counter_get);
        _hStage1MsSetMotion = new FhMethodHandle<MsSetMotionProbeDelegate>(
            this, "FFX.exe", FhFfx.FhCall.__addr_MsSetMotion, h_stage1_ms_set_motion);

        try_install_one_stage1_probe(_hStage1MsActionRequest,           "MsActionRequest");
        try_install_one_stage1_probe(_hStage1MsCalcCommand,             "MsCalcCommand");
        try_install_one_stage1_probe(_hStage1MsCheckStatusBeforeAction, "MsCheckStatusBeforeAction");
        try_install_one_stage1_probe(_hStage1MsLimitTypeDamageCheck,    "MsLimitTypeDamageCheck");
        try_install_one_stage1_probe(_hStage1MsAtelRequestMagicCamera,  "MsAtelRequestMagicCamera");
        try_install_one_stage1_probe(_hStage1OpEtBattleGenkoCounterGet, "op_et_battle_genko_counter_get");
        try_install_one_stage1_probe(_hStage1MsSetMotion,               "MsSetMotion");

        _stage1ProbesInstalled = true;
        _logger.Info("[Parry] Stage-1 native probes installed (NativeProbeLogging=true). Output is in the session debug log.");
    }

    private void try_install_one_stage1_probe<TDelegate>(FhMethodHandle<TDelegate>? handle, string label) where TDelegate : Delegate
    {
        if (handle == null) return;
        try
        {
            handle.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook Stage-1 probe {label}: {ex.Message}");
        }
    }

    private uint h_stage1_ms_action_request(int target_id, int attacker_id, int p3, int p4, int p5, int p6)
    {
        uint result = _hStage1MsActionRequest!.orig_fptr.Invoke(target_id, attacker_id, p3, p4, p5, p6);

        if (_optionNativeProbeLogging
            && _throttleMsActionRequest.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"target_id={target_id} attacker_id={attacker_id} p3=0x{p3:X} p4=0x{p4:X} p5=0x{p5:X} p6=0x{p6:X} ret=0x{result:X}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsActionRequest", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsActionRequest", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }

    private void h_stage1_ms_calc_command()
    {
        _hStage1MsCalcCommand!.orig_fptr.Invoke();

        if (_optionNativeProbeLogging
            && _throttleMsCalcCommand.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsCalcCommand", string.Empty, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsCalcCommand", _debugFrameIndex, ex.Message));
            }
        }
    }

    private int h_stage1_ms_check_status_before_action()
    {
        int result = _hStage1MsCheckStatusBeforeAction!.orig_fptr.Invoke();

        if (_optionNativeProbeLogging
            && _throttleMsCheckStatusBeforeAction.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"ret=0x{result:X}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsCheckStatusBeforeAction", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsCheckStatusBeforeAction", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }

    private int h_stage1_ms_limit_type_damage_check(
        int attacker_id, nint attacker, int target_id, nint target, int p5, int p6, int p7)
    {
        int result = _hStage1MsLimitTypeDamageCheck!.orig_fptr.Invoke(
            attacker_id, attacker, target_id, target, p5, p6, p7);

        if (_optionNativeProbeLogging
            && _throttleMsLimitTypeDamageCheck.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"attacker_id={attacker_id} target_id={target_id} attacker_ptr=0x{attacker:X} target_ptr=0x{target:X} p5=0x{p5:X} p6=0x{p6:X} p7=0x{p7:X} ret={result}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsLimitTypeDamageCheck", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsLimitTypeDamageCheck", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }

    private void h_stage1_ms_atel_request_magic_camera()
    {
        _hStage1MsAtelRequestMagicCamera!.orig_fptr.Invoke();

        if (_optionNativeProbeLogging
            && _throttleMsAtelRequestMagicCamera.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsAtelRequestMagicCamera", string.Empty, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsAtelRequestMagicCamera", _debugFrameIndex, ex.Message));
            }
        }
    }

    private int h_stage1_op_et_battle_genko_counter_get()
    {
        int result = _hStage1OpEtBattleGenkoCounterGet!.orig_fptr.Invoke();

        if (_optionNativeProbeLogging
            && _throttleOpEtBattleGenkoCounterGet.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"ret=0x{result:X}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "op_et_battle_genko_counter_get", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("op_et_battle_genko_counter_get", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }

    private uint h_stage1_ms_set_motion(
        int p1, int p2, int chr_id, byte p4, int p5, int p6, int p7)
    {
        uint result = _hStage1MsSetMotion!.orig_fptr.Invoke(p1, p2, chr_id, p4, p5, p6, p7);

        if (_optionNativeProbeLogging
            && _throttleMsSetMotion.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"p1=0x{p1:X} p2=0x{p2:X} chr_id={chr_id} p4=0x{p4:X} p5=0x{p5:X} p6=0x{p6:X} p7=0x{p7:X} ret=0x{result:X}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsSetMotion", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsSetMotion", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }
}
