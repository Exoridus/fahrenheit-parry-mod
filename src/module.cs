// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

[FhLoad(FhGameId.FFX)]
public unsafe sealed partial class ParryModule : FhModule {
    private const int   PartyActorCapacity        = 10;
    private const int   EnemyActorCapacity        = 10;
    private const uint  PlayerTargetMask          = (1u << PartyActorCapacity) - 1u;
    private const int   MaxAttackCueScan          = 64;
    private const int   SoundResetFrames          = 6;
    private const int   IndicatorFlashFrames      = 45;
    private const float BattleFrameRate           = 30f;
    private const float FrameDurationSeconds      = 1f / BattleFrameRate;
    private const float WindowMinSeconds          = 0.5f;
    private const float WindowMaxSeconds          = 2f;
    private const float WindowStepSeconds         = 0.1f;
    private const float LeadPhysicalMinSeconds    = 0f;
    private const float LeadPhysicalMaxSeconds    = 0.5f;
    private const float LeadMagicMinSeconds       = 0f;
    private const float LeadMagicMaxSeconds       = 1.0f;
    private const float OverdriveBoostPercent     = 0.05f;
    private const float OverlayAnimDurationSeconds  = 0.4f;
    private const float OverlayScaleDurationSeconds = 0.18f;
    private const float ResolveWindowMinSeconds   = 0.2f;
    private const float ResolveWindowMaxSeconds   = 2f;
    private const int   TimingLogMaxSamplesPerMinute = 240;
    private const long  TimingLogMaxBytes = 8L * 1024L * 1024L;
    private const int   DebugLogRingCapacity = 500;
    private const int   CueHistoryRingCapacity = 64;

    private enum ParryTimingMode {
        FixedWindow,
        ApplyDamageClamp
    }

    private enum ParryOverlayState {
        Hidden,
        Parry,
        Success,
        Failure
    }

    // Runtime-only mutable state lives here to keep transitions centralized and auditable.
    private struct ParryRuntimeState {
        public bool ParryWindowActive;
        public byte CurrentAttackerId;
        public byte CurrentCueIndex;
        public uint CurrentPartyTargetMask;
        public int ParryWindowFrames;
        public bool AwaitingTurnEnd;
        public bool LeadPending;
        public int LeadFramesRemaining;
        public byte LeadAttackerId;
        public int ParryWindowElapsedFrames;
        public int PendingLeadFramesApplied;
        public int ParryWindowDebounceFrames;
        public bool ParryInputDebounced;
        public bool ParryWindowSucceeded;
        public bool SuccessIndicatorActive;

        public int PendingSoundSlot;
        public int PendingSoundFrames;
        public uint PendingNegateMask;
        public int PendingNegateTimeoutFrames;

        public bool AttackCueClampWarned;

        public int SuccessFlashFrames;
        public int FailureFlashFrames;
        public float OverlayAnimProgress;
        public float OverlayScaleProgress;
        public ParryOverlayState OverlayState;
        public ParryOverlayState LastOverlayState;

        public ParryTimingTimeline? ActiveTiming;
        public int TimingLogSamplesInWindow;
        public DateTime TimingLogWindowStartUtc;
        public bool TimingLogDropNotified;

        public static ParryRuntimeState CreateDefault() => new() {
            PendingSoundSlot = -1,
            OverlayState = ParryOverlayState.Hidden,
            LastOverlayState = ParryOverlayState.Hidden,
            TimingLogWindowStartUtc = DateTime.UtcNow
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
    private float _optionWindowSeconds    = 1.0f;
    private float _optionLeadPhysicalSeconds = 0.10f;
    private float _optionLeadMagicSeconds = 0.30f;
    private ParryTimingMode _optionTimingMode = ParryTimingMode.ApplyDamageClamp;
    private float _optionResolveWindowSeconds = 0.8f;

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
    private string? _timingLogPath;
    private readonly List<DebugLogEntry> _debugLog = new(DebugLogRingCapacity);
    private readonly List<DebugCueSnapshot> _debugCueSnapshots = new(MaxAttackCueScan);
    private readonly List<DebugCueSnapshot> _debugCueScratch = new(MaxAttackCueScan);
    private readonly List<DebugCueHistoryEntry> _debugCueHistory = new(CueHistoryRingCapacity);
    private ulong _debugFrameIndex;
    private ulong _debugBattleFrameIndex;
    private bool _debugBattleActive;
    private bool _debugGameSaveLoaded;
    private bool _debugAutoScroll = true;
    private bool _debugCueAutoScroll = true;
    private float _debugStatePanelRatio = 0.38f;
    private float _debugCuePanelRatio = 0.68f;
    private int _debugCueTurnId;

    private readonly JsonSerializerOptions _timingJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ParryModule() {
        settings = new FhSettingsCategory("fhparry", [
            new FhSettingCustomRenderer("enabled", render_setting_enabled),
            new FhSettingCustomRenderer("timing_mode", render_setting_timing_mode),
            new FhSettingCustomRenderer("window_seconds", render_setting_window),
            new FhSettingCustomRenderer("resolve_window", render_setting_resolve_window),
            new FhSettingCustomRenderer("indicator", render_setting_indicator),
            new FhSettingCustomRenderer("audio", render_setting_audio),
            new FhSettingCustomRenderer("ctb", render_setting_overdrive_boost),
            new FhSettingCustomRenderer("logging", render_setting_logging),
            new FhSettingCustomRenderer("debug_overlay", render_setting_debug_overlay),
            new FhSettingCustomRenderer("negate", render_setting_negate),
            new FhSettingCustomRenderer("lead_physical", render_setting_lead_physical),
            new FhSettingCustomRenderer("lead_magic", render_setting_lead_magic),
            new FhSettingCustomRenderer("future", render_setting_future)
        ]);
    }

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        _bannerPathParry = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "parry.png");
        _bannerPathSuccess = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "success.png");
        _bannerPathFail = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "toobad.png");
        _timingLogPath = Path.Combine(mod_context.Paths.ModDir.FullName, "parry_timings.jsonl");

        _logger.Info("ParryPrototype ready. Adjust options via Mod Config (F7).");
        return true;
    }

    public override void pre_update() {
        _debugFrameIndex++;
        update_debug_save_loaded_state();
        update_debug_battle_session_state();
        monitor_cue_transitions();

        if (!_optionEnabled) {
            reset_runtime_state("disabled", clearFeedbackFlashes: true, clearDamageFlags: true);
            update_overlay_animation_state();
            return;
        }

        if (_runtime.ParryWindowDebounceFrames > 0) {
            _runtime.ParryWindowDebounceFrames--;
        }

        bool hasEnemyCue = monitor_attack_cues();
        process_lead_pending();
        monitor_damage_resolves();
        process_pending_negation();
        update_sound_flag();

        if (_runtime.ParryWindowActive) {
            _runtime.ParryWindowElapsedFrames++;
            _runtime.ParryWindowFrames--;
            if (_runtime.ParryWindowFrames <= 0) {
                trigger_failure_feedback();
                end_parry_window("timeout");
            }
            else if (!_runtime.ParryInputDebounced && FhApi.Input.r1.just_pressed) {
                _runtime.ParryInputDebounced = true;
                on_parry_success();
            }
        }
        else if (!_runtime.LeadPending && _runtime.AwaitingTurnEnd && !hasEnemyCue) {
            clear_awaiting_turn_end("Awaiting turn end cleared after no-cue update.");
        }

        if (_runtime.SuccessFlashFrames > 0) _runtime.SuccessFlashFrames--;
        if (_runtime.FailureFlashFrames > 0) _runtime.FailureFlashFrames--;

        update_overlay_animation_state();
    }

    public override void render_imgui() {
        render_parry_window_overlay();
        render_debug_overlay();
    }

    private void reset_runtime_state(string timingReason, bool clearFeedbackFlashes, bool clearDamageFlags) {
        finalize_timing_capture(timingReason);

        end_pending_sound_feedback(forceResetSound: true);

        _runtime.ParryWindowActive = false;
        _runtime.CurrentAttackerId = 0;
        _runtime.CurrentCueIndex = 0;
        _runtime.CurrentPartyTargetMask = 0;
        _runtime.ParryWindowFrames = 0;
        _runtime.AwaitingTurnEnd = false;
        _runtime.LeadPending = false;
        _runtime.LeadFramesRemaining = 0;
        _runtime.LeadAttackerId = 0;
        _runtime.ParryWindowElapsedFrames = 0;
        _runtime.PendingLeadFramesApplied = 0;
        _runtime.ParryWindowDebounceFrames = 0;
        _runtime.ParryInputDebounced = false;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.PendingNegateMask = 0;
        _runtime.PendingNegateTimeoutFrames = 0;

        if (clearFeedbackFlashes) {
            _runtime.SuccessFlashFrames = 0;
            _runtime.FailureFlashFrames = 0;
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

    private static int seconds_to_frames(float seconds) {
        int frames = (int)MathF.Round(seconds * BattleFrameRate);
        return Math.Max(frames, 0);
    }

    private static float frames_to_seconds(int frames) {
        return frames / BattleFrameRate;
    }

    private sealed class DebugLogEntry {
        public DateTime TimestampLocal { get; set; }
        public ulong FrameIndex { get; set; }
        public string Message { get; set; } = string.Empty;
        public int RepeatCount { get; set; } = 1;
    }
}
