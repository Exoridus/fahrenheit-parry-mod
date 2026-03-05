// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

[FhLoad(FhGameId.FFX)]
public unsafe sealed partial class ParryModule : FhModule {
    private const int   PartyActorCapacity        = 10;
    private const int   EnemyActorCapacity        = 10;
    private const uint  PlayerTargetMask          = (1u << PartyActorCapacity) - 1u;
    private const int   MaxAttackCueScan          = 64;
    private const float BattleFrameRate           = 30f;
    private const float FrameDurationSeconds      = 1f / BattleFrameRate;
    private const float SoundResetSeconds         = 6f * FrameDurationSeconds;
    private const float IndicatorFlashSeconds     = 45f * FrameDurationSeconds;
    private const float OverdriveBoostPercent     = 0.05f;
    private const float OverlayAnimDurationSeconds  = 0.4f;
    private const float OverlayScaleDurationSeconds = 0.18f;
    private const int   DebugLogRingCapacity = 500;
    private const int   CueHistoryRingCapacity = 64;
    private const int   DebugTurnRowCapacity = 500;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SgMainLoop(float delta);

    private enum ParryOverlayState {
        Hidden,
        Parry,
        Success,
        Failure
    }

    private enum CommandIdSource {
        None,
        CueCommandInfo,
        CueOffsetCandidate,
        LastComFallback
    }

    private enum CommandIdConfidence {
        None,
        Low,
        Medium,
        High
    }

    private readonly struct ResolvedCommandInfo {
        public readonly ushort CommandId;
        public readonly string Label;
        public readonly string Kind;
        public readonly CommandIdSource Source;
        public readonly CommandIdConfidence Confidence;

        public bool HasCommandId => CommandId != 0;
        public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

        public ResolvedCommandInfo(
            ushort commandId,
            string label,
            string kind,
            CommandIdSource source,
            CommandIdConfidence confidence) {
            CommandId = commandId;
            Label = label ?? string.Empty;
            Kind = kind ?? string.Empty;
            Source = source;
            Confidence = confidence;
        }

        public static ResolvedCommandInfo None => new(
            commandId: 0,
            label: string.Empty,
            kind: string.Empty,
            source: CommandIdSource.None,
            confidence: CommandIdConfidence.None);
    }

    private readonly struct ParryInputContext {
        public readonly bool HasParryableCue;
        public readonly AttackCue Cue;
        public readonly byte CueIndex;
        public readonly uint PartyMask;

        public ParryInputContext(bool hasParryableCue, AttackCue cue, byte cueIndex, uint partyMask) {
            HasParryableCue = hasParryableCue;
            Cue = cue;
            CueIndex = cueIndex;
            PartyMask = partyMask;
        }

        public static ParryInputContext None => new(false, default, 0, 0);
    }

    // Runtime-only mutable state lives here to keep transitions centralized and auditable.
    private struct ParryRuntimeState {
        public bool ParryWindowActive;
        public byte CurrentAttackerId;
        public byte CurrentCueIndex;
        public uint CurrentPartyTargetMask;
        public float ParryWindowRemainingSeconds;
        public bool AwaitingTurnEnd;
        public float ParryWindowElapsedSeconds;
        public bool ParryWindowSucceeded;
        public bool SuccessIndicatorActive;
        public int SpamTierIndex;
        public float SpamTierResetRemainingSeconds;
        public bool SpamReleaseArmed;

        public int PendingSoundSlot;
        public float PendingSoundSeconds;
        public bool AttackCueClampWarned;

        public float SuccessFlashSeconds;
        public float FailureFlashSeconds;
        public float OverlayAnimProgress;
        public float OverlayScaleProgress;
        public ParryOverlayState OverlayState;
        public ParryOverlayState LastOverlayState;

        public static ParryRuntimeState CreateDefault() => new() {
            PendingSoundSlot = -1,
            OverlayState = ParryOverlayState.Hidden,
            LastOverlayState = ParryOverlayState.Hidden
        };
    }

    private bool _optionEnabled           = true;
    private bool _optionIndicator         = true;
    private bool _optionSound             = true;
    private bool _optionLogging           = true;
    private bool _optionOverdriveBoost    = true;
    private bool _optionNegateDamage      = true;
    private bool _optionDebugOverlay      =
#if DEBUG
        true;
#else
        false;
#endif
    private ParryDifficulty _optionDifficulty = ParryDifficulty.Normal;

    private FhTexture? _bannerTextureParry;
    private FhTexture? _bannerTextureSuccess;
    private FhTexture? _bannerTextureFail;
    private string? _bannerPathParry;
    private string? _bannerPathSuccess;
    private string? _bannerPathFail;
    private bool _bannerParryWarned;
    private bool _bannerSuccessWarned;
    private bool _bannerFailWarned;

    private readonly bool[] _damageEventActive = new bool[PartyActorCapacity];
    private ParryRuntimeState _runtime = ParryRuntimeState.CreateDefault();
    private readonly List<DebugLogEntry> _debugLog = new(DebugLogRingCapacity);
    private readonly List<DebugCueSnapshot> _debugCueSnapshots = new(MaxAttackCueScan);
    private readonly List<DebugCueSnapshot> _debugCueScratch = new(MaxAttackCueScan);
    private readonly List<DebugCueHistoryEntry> _debugCueHistory = new(CueHistoryRingCapacity);
    private readonly TurnTimelineTracker _turnTimeline = new(DebugTurnRowCapacity);
    private readonly TurnTimelineRuntimeEventSource _turnRuntimeEvents = new();
    private readonly FfxDataMappings _dataMappings = new();
    private readonly List<DebugCueSnapshot> _debugHookCueScratch = new(MaxAttackCueScan);
    private readonly List<TurnTimelineCueObservation> _debugTimelineCueScratch = new(MaxAttackCueScan);
    private readonly List<TurnTimelineEvent> _debugTimelineEventScratch = new(64);
    private readonly List<TurnTimelineRuntimeSignal> _debugRuntimeSignalScratch = new(128);
    private readonly ParrySpamController _spamController = new();
    private double _simulationClockSeconds;
    private ulong _debugFrameIndex;
    private ulong _debugBattleFrameIndex;
    private bool _debugBattleActive;
    private bool _debugGameSaveLoaded;
    private bool _debugGameplayReady;
    private bool _debugAutoScroll = true;
    private bool _debugCueAutoScroll = true;
    private float _debugCuePanelRatio = 0.50f;
    private int _debugCueTurnId;
    private string _dataMappingStatus = "No data mappings loaded.";
    private readonly IParryTimeSource _timeSource = new SimulationDeltaTimeSource(FrameDurationSeconds);
    private readonly FhMethodHandle<SgMainLoop> _hMainLoop;
    private readonly FhMethodHandle<FhFfx.FhCall.MsExeInputCue> _hMsExeInputCue;

    public ParryModule() {
        _hMainLoop = new FhMethodHandle<SgMainLoop>(this, "FFX.exe", 0x420C00, h_main_loop_timing);
        _hMsExeInputCue = new FhMethodHandle<FhFfx.FhCall.MsExeInputCue>(this, "FFX.exe", FhFfx.FhCall.__addr_MsExeInputCue, h_ms_exe_input_cue);

        settings = new FhSettingsCategory("fhparry", [
            new FhSettingCustomRenderer("enabled", render_setting_enabled),
            new FhSettingCustomRenderer("difficulty", render_setting_difficulty),
            new FhSettingCustomRenderer("indicator", render_setting_indicator),
            new FhSettingCustomRenderer("audio", render_setting_audio),
            new FhSettingCustomRenderer("ctb", render_setting_overdrive_boost),
            new FhSettingCustomRenderer("logging", render_setting_logging),
            new FhSettingCustomRenderer("debug_overlay", render_setting_debug_overlay),
            new FhSettingCustomRenderer("negate", render_setting_negate),
            new FhSettingCustomRenderer("future", render_setting_future)
        ]);
    }

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        _bannerPathParry = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "parry.png");
        _bannerPathSuccess = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "success.png");
        _bannerPathFail = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "toobad.png");
        initialize_data_mappings(mod_context);

        try {
            _hMainLoop.hook();
        }
        catch (Exception ex) {
            _logger.Warning($"[Parry] Could not hook Sg_MainLoop for simulation delta timing (falling back to fixed timestep): {ex.Message}");
        }

        try {
            _hMsExeInputCue.hook();
        }
        catch (Exception ex) {
            _logger.Warning($"[Parry] Could not hook MsExeInputCue (continuing without native dispatch signal): {ex.Message}");
        }

        _logger.Info("ParryPrototype ready. Adjust options via Mod Config (F7).");
        return true;
    }

    public override void pre_update() {
        _debugFrameIndex++;
        float deltaSeconds = _timeSource.GetDeltaSeconds();
        _simulationClockSeconds += deltaSeconds;
        update_debug_save_loaded_state();
        update_debug_battle_session_state();
        monitor_cue_transitions();

        if (!_optionEnabled) {
            reset_runtime_state("disabled", clearFeedbackFlashes: true, clearDamageFlags: true);
            process_turn_runtime_events();
            update_overlay_animation_state(deltaSeconds);
            return;
        }

        bool hasEnemyCue = monitor_attack_cues();
        monitor_damage_resolves();
        update_sound_flag(deltaSeconds);
        advance_spam_penalty_timers(deltaSeconds);

        ParryInputContext parryInput = capture_parry_input_context();

        // Handle release first so if both release/press are visible in the same polling step,
        // we treat it as a tap-spam cycle and allow escalation.
        if (FhApi.Input.r1.just_released) {
            handle_parry_input_release(parryInput);
        }

        if (FhApi.Input.r1.just_pressed) {
            handle_parry_input_press(parryInput);
        }

        if (_runtime.ParryWindowActive) {
            _runtime.ParryWindowElapsedSeconds += deltaSeconds;
            _runtime.ParryWindowRemainingSeconds = MathF.Max(0f, _runtime.ParryWindowRemainingSeconds - deltaSeconds);
            if (_runtime.ParryWindowRemainingSeconds <= 0f) {
                end_parry_window("input_window_expired");
            }
        }
        else if (_runtime.AwaitingTurnEnd && !hasEnemyCue) {
            clear_awaiting_turn_end("Awaiting turn end cleared after no-cue update.");
        }

        if (_runtime.SuccessFlashSeconds > 0f) _runtime.SuccessFlashSeconds = MathF.Max(0f, _runtime.SuccessFlashSeconds - deltaSeconds);
        if (_runtime.FailureFlashSeconds > 0f) _runtime.FailureFlashSeconds = MathF.Max(0f, _runtime.FailureFlashSeconds - deltaSeconds);

        validate_runtime_state();
        process_turn_runtime_events();
        update_overlay_animation_state(deltaSeconds);
    }

    public override void render_imgui() {
        render_parry_window_overlay();
        render_debug_overlay();
    }

    private void reset_runtime_state(string timingReason, bool clearFeedbackFlashes, bool clearDamageFlags) {
        end_pending_sound_feedback(forceResetSound: true);
        _spamController.Reset("runtime_reset");

        _runtime.ParryWindowActive = false;
        _runtime.CurrentAttackerId = 0;
        _runtime.CurrentCueIndex = 0;
        _runtime.CurrentPartyTargetMask = 0;
        _runtime.ParryWindowRemainingSeconds = 0f;
        _runtime.AwaitingTurnEnd = false;
        _runtime.ParryWindowElapsedSeconds = 0f;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.SpamTierIndex = 0;
        _runtime.SpamTierResetRemainingSeconds = 0f;
        _runtime.SpamReleaseArmed = false;

        if (clearFeedbackFlashes) {
            _runtime.SuccessFlashSeconds = 0f;
            _runtime.FailureFlashSeconds = 0f;
        }

        if (clearDamageFlags) {
            Array.Clear(_damageEventActive);
        }
    }

    private void log_debug(string message) {
        bool appended = append_debug_event(message);

        if (_optionLogging && appended) {
            _logger.Info($"[Parry] {message}");
        }
    }

    private void h_main_loop_timing(float delta) {
        _timeSource.CaptureSimulationDelta(delta);
        _hMainLoop.orig_fptr(delta);
    }

    private DateTime current_gameplay_timestamp() {
        return DateTime.UnixEpoch + TimeSpan.FromSeconds(_simulationClockSeconds);
    }

    private double current_gameplay_seconds() {
        return _simulationClockSeconds;
    }

    private void validate_runtime_state() {
        _runtime.SpamTierIndex = ParryDifficultyModel.ClampTierIndex(_spamController.TierIndex);
        _runtime.SpamTierResetRemainingSeconds = MathF.Max(0f, _spamController.CalmResetRemainingSeconds);
        _runtime.SpamReleaseArmed = _spamController.ReleaseArmed;
        _runtime.ParryWindowRemainingSeconds = MathF.Max(0f, _runtime.ParryWindowRemainingSeconds);
        _runtime.ParryWindowElapsedSeconds = MathF.Max(0f, _runtime.ParryWindowElapsedSeconds);
        _runtime.SuccessFlashSeconds = MathF.Max(0f, _runtime.SuccessFlashSeconds);
        _runtime.FailureFlashSeconds = MathF.Max(0f, _runtime.FailureFlashSeconds);
        _runtime.PendingSoundSeconds = MathF.Max(0f, _runtime.PendingSoundSeconds);

        if (!_runtime.ParryWindowActive && (_runtime.ParryWindowRemainingSeconds > 0f || _runtime.ParryWindowElapsedSeconds > 0f)) {
            _runtime.ParryWindowRemainingSeconds = 0f;
            _runtime.ParryWindowElapsedSeconds = 0f;
        }

        if (!_runtime.AwaitingTurnEnd && _runtime.CurrentPartyTargetMask != 0) {
            _runtime.CurrentPartyTargetMask = 0;
        }
    }

    private sealed class DebugLogEntry {
        public DateTime TimestampLocal { get; set; }
        public double SimulationSeconds { get; set; }
        public ulong FrameIndex { get; set; }
        public string Message { get; set; } = string.Empty;
        public int RepeatCount { get; set; } = 1;
    }
}
