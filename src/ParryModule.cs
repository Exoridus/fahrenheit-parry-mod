// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

[FhLoad(FhGameId.FFX)]
public unsafe sealed partial class ParryModule : FhModule
{
    private const int PartyActorCapacity = 10;
    private const int EnemyActorCapacity = 10;
    private const uint PlayerTargetMask = (1u << PartyActorCapacity) - 1u;
    private const int MaxAttackCueScan = 64;
    private const float BattleFrameRate = 30f;
    private const float FrameDurationSeconds = 1f / BattleFrameRate;
    private const float ParriedTextSeconds = 1.0f;
    private const float ParryMissedTextSeconds = 1.0f;
    private const float OverdriveBoostPercent = 0.05f;
    private const int DebugLogRingCapacity = 500;
    private const int CueHistoryRingCapacity = 64;
    private const int DebugTurnRowCapacity = 500;
    private const ushort StartupSkipTitleRoomId = 23;
    private const uint StartupSkipMemochekEventId = 348;
    private const uint StartupSkipLoopdemoEventId = 349;
    private const int StartupSkipProgressFlagOffset = 0xC88;
    private const float StartupLayerSuppressWindowSeconds = 8.0f;
    private const int StartupLayerPrimaryId = 13;
    private const int StartupLayerSecondaryAId = 2;
    private const int StartupLayerSecondaryBId = 4;
    private const float StartupForceSkipWindowSeconds = 20.0f;
    private const int StartupTest20PatchRequiredCodeLength = 0x381;
    private const int StartupForceRetryFrames = 3;
    private const int StartupForceMaxAttempts = 120;
    private const float StartupProbeWindowSeconds = 30.0f;
    private const int StartupProbePeriodicFrames = 5;
    private static readonly bool StartupTest20ScriptPatchEnabled = false;
    private static readonly byte[] StartupPatchWait0Call = new byte[] { 0xAE, 0x00, 0x00, 0xD8, 0x00, 0x00 };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SgMainLoop(float delta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AtelEventSetUp(uint eventId);
    private delegate char* AtelGetEventName(uint eventId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void MapShow2DLayerExec(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MapShow2DLayerRetInt(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float MapShow2DLayerRetFloat(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NeedShowJapanLogo();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MovieStopProg();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CommonSetSplashSpriteExec(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CommonSetSplashSpriteRetInt(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float CommonSetSplashSpriteRetFloat(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CommonHideSplashExec(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CommonHideSplashRetInt(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float CommonHideSplashRetFloat(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack);

    private enum CommandIdSource
    {
        None,
        CueCommandInfo,
        CueOffsetCandidate,
        LastComFallback
    }

    private enum CommandIdConfidence
    {
        None,
        Low,
        Medium,
        High
    }

    private readonly struct ResolvedCommandInfo
    {
        public readonly ushort CommandId;
        public readonly string Label;
        public readonly string Kind;
        public readonly string DamageType;
        public readonly CommandIdSource Source;
        public readonly CommandIdConfidence Confidence;

        public bool HasCommandId => CommandId != 0;
        public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

        public ResolvedCommandInfo(
            ushort commandId,
            string label,
            string kind,
            string damageType,
            CommandIdSource source,
            CommandIdConfidence confidence)
        {
            CommandId = commandId;
            Label = label ?? string.Empty;
            Kind = kind ?? string.Empty;
            DamageType = damageType ?? string.Empty;
            Source = source;
            Confidence = confidence;
        }

        public static ResolvedCommandInfo None => new(
            commandId: 0,
            label: string.Empty,
            kind: string.Empty,
            damageType: string.Empty,
            source: CommandIdSource.None,
            confidence: CommandIdConfidence.None);
    }

    private readonly struct ParryInputContext
    {
        public readonly bool HasParryableCue;
        public readonly AttackCue Cue;
        public readonly byte CueIndex;
        public readonly uint PartyMask;

        public ParryInputContext(bool hasParryableCue, AttackCue cue, byte cueIndex, uint partyMask)
        {
            HasParryableCue = hasParryableCue;
            Cue = cue;
            CueIndex = cueIndex;
            PartyMask = partyMask;
        }

        public static ParryInputContext None => new(false, default, 0, 0);
    }

    private readonly struct StartupScriptPatch
    {
        public readonly int Offset;
        public readonly string Label;
        public readonly byte[] Expected;

        public StartupScriptPatch(int offset, string label, byte[] expected)
        {
            Offset = offset;
            Label = label;
            Expected = expected;
        }
    }

    private static readonly StartupScriptPatch[] StartupTest20SplashPatches = new StartupScriptPatch[] {
        new StartupScriptPatch(0x032A, "title-layer13-a", new byte[] { 0xAE, 0x0D, 0x00, 0xD8, 0x0F, 0x80 }),
        new StartupScriptPatch(0x0333, "title-layer13-b", new byte[] { 0xAE, 0x0D, 0x00, 0xD8, 0x0F, 0x80 }),
        new StartupScriptPatch(0x033C, "title-layer13-c", new byte[] { 0xAE, 0x0D, 0x00, 0xD8, 0x0F, 0x80 }),
        new StartupScriptPatch(0x0369, "title-layer2", new byte[] { 0xAE, 0x02, 0x00, 0xD8, 0x0F, 0x80 }),
        new StartupScriptPatch(0x0372, "title-layer4-a", new byte[] { 0xAE, 0x04, 0x00, 0xD8, 0x0F, 0x80 }),
        new StartupScriptPatch(0x037B, "title-layer4-b", new byte[] { 0xAE, 0x04, 0x00, 0xD8, 0x0F, 0x80 }),
    };

    // Runtime-only mutable state lives here to keep transitions centralized and auditable.
    private struct ParryRuntimeState
    {
        public bool ParryWindowActive;
        public byte CurrentAttackerId;
        public byte CurrentCueIndex;
        public uint CurrentPartyTargetMask;
        public uint CurrentCueSignature;
        public float ParryWindowRemainingSeconds;
        public bool AwaitingTurnEnd;
        public float ParryWindowElapsedSeconds;
        public bool ParryWindowSucceeded;
        public bool SuccessIndicatorActive;

        public bool AttackCueClampWarned;
        public float ParriedTextRemainingSeconds;
        public float ParryMissedTextRemainingSeconds;
        public int LastParriedTargetSlot;
        public ulong LastDispatchConsumedFrame;
        public byte LastDispatchConsumedAttackerId;
        public byte LastDispatchConsumedQueueIndex;
        public ulong LastCorrelationSkipFrame;

        public ulong CueFirstSeenFrame;
        public ulong WindowOpenFrame;
        public float WindowOpenTimestampSeconds;

        public static ParryRuntimeState CreateDefault() => new()
        {
            LastParriedTargetSlot = -1,
            LastDispatchConsumedQueueIndex = 0xFF
        };
    }

    private bool _optionEnabled = true;
    private bool _optionSound = true;
    private float _optionAudioVolume = 1.0f;
    private bool _optionLogging = true;
    private bool _optionParryStateHud = true;
    private bool _optionOverdriveBoost = true;
    private bool _optionNegateDamage = true;
    private bool _optionPenaltyEnabled = true;
    private bool _optionStartupSkipForceTitle = true;
    private bool _optionStartupProbeMode = false;
    private bool _optionDebugOverlay =
#if DEBUG
        true;
#else
        false;
#endif
    private ParryDifficulty _optionDifficulty = ParryDifficulty.Normal;
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
    private readonly Dictionary<string, ulong> _debugMessageLastEmitFrame = new(StringComparer.Ordinal);
    private readonly ParrySpamController _spamController = new();
    private readonly Dictionary<string, int> _impactCorrelationRejectCounts = new(StringComparer.Ordinal);
    private int _impactCorrelationMatchedCount;
    private int _impactCorrelationRejectedCount;
    private string _impactCorrelationLastRejectReason = "None";
    private ulong _impactCorrelationLastSummaryFrame;
    private double _simulationClockSeconds;
    private ulong _debugFrameIndex;
    private ulong _debugBattleFrameIndex;
    private bool _debugBattleActive;
    private bool _debugBattleSessionFirstCueSeen;
    private bool _debugGameSaveLoaded;
    private bool _debugGameplayReady;
    private bool _startupSkipStatusLogged;
    private bool _startupMovieSkipApplied;
    private int _startupForceAttemptCount;
    private ulong _startupForceLastAttemptFrame;
    private int _startupEventTraceCount;
    private int _startupLayerTraceCount;
    private int _startupLayerHookProbeTraceCount;
    private int _startupLayerArgTraceCount;
    private ulong _startupHideLastFrame;
    private int _startupHideLastLayer = -1;
    private byte _startupNeedAutoSaveLast;
    private bool _startupNeedAutoSaveLogged;
    private bool _startupLayerHooksEnabled;
    private int _startupCommonTraceCount;
    private bool _startupTest20PatchApplied;
    private bool _startupTest20PatchMismatchLogged;
    private bool _debugAutoScroll = true;
    private bool _debugCueAutoScroll = true;
    private float _debugCuePanelRatio = 0.50f;
    private int _debugCueTurnId;
    private string _dataMappingStatus = "No data mappings loaded.";
    private readonly Random _rng = new();
    private readonly List<WavClip> _parryAudioClips = new(8);
    private string _settingsFilePath = string.Empty;
    private StreamWriter? _sessionDebugLogWriter;
    private StreamWriter? _sessionTimelineLogWriter;
    private StreamWriter? _sessionStartupProbeWriter;
    private string _sessionLogsRoot = string.Empty;
    private string _sessionLogDirectory = string.Empty;
    private bool _sessionLogDisabled;
    private bool _sessionRetentionPruned;
    private bool _startupProbeHeaderWritten;
    private bool _startupProbeCompleted;
    private ulong _startupProbeLastFrame;
    private string _startupProbeLastSignature = string.Empty;
    private string? _audioResourcesDir;
    private string? _fontResourcesDir;
    private string? _overlayFontPath;
    private ImFontPtr _overlayFont;
    private bool _overlayFontsInitialized;
    private bool _overlayFontWarningIssued;
    private uint _battleSceneRevision;
    private ulong _lastBattleSceneRefreshFrame;
    private readonly IParryTimeSource _timeSource = new SimulationDeltaTimeSource(FrameDurationSeconds);
    private readonly FhMethodHandle<SgMainLoop> _hMainLoop;
    private readonly FhMethodHandle<FhFfx.FhCall.MsExeInputCue> _hMsExeInputCue;
    private readonly FhMethodHandle<AtelEventSetUp> _hAtelEventSetUp;
    private readonly FhMethodHandle<NeedShowJapanLogo> _hNeedShowJapanLogo;
    private FhMethodHandle<MapShow2DLayerExec>? _hMapShow2DLayerExec;
    private FhMethodHandle<MapShow2DLayerRetInt>? _hMapShow2DLayerRetInt;
    private FhMethodHandle<MapShow2DLayerRetFloat>? _hMapShow2DLayerRetFloat;
    private FhMethodHandle<CommonSetSplashSpriteExec>? _hCommonSetSplashSpriteExec;
    private FhMethodHandle<CommonSetSplashSpriteRetInt>? _hCommonSetSplashSpriteRetInt;
    private FhMethodHandle<CommonSetSplashSpriteRetFloat>? _hCommonSetSplashSpriteRetFloat;
    private FhMethodHandle<CommonHideSplashExec>? _hCommonHideSplashExec;
    private FhMethodHandle<CommonHideSplashRetInt>? _hCommonHideSplashRetInt;
    private FhMethodHandle<CommonHideSplashRetFloat>? _hCommonHideSplashRetFloat;

    public ParryModule()
    {
        _hMainLoop = new FhMethodHandle<SgMainLoop>(this, "FFX.exe", 0x420C00, h_main_loop_timing);           // Sg_MainLoop — game update tick; used for simulation delta timing
        _hMsExeInputCue = new FhMethodHandle<FhFfx.FhCall.MsExeInputCue>(this, "FFX.exe", FhFfx.FhCall.__addr_MsExeInputCue, h_ms_exe_input_cue);
        _hAtelEventSetUp = new FhMethodHandle<AtelEventSetUp>(this, "FFX.exe", 0x472e90, h_startup_event_setup); // AtelEventSetUp — Atel scripting event dispatch; intercepted for startup skip
        _hNeedShowJapanLogo = new FhMethodHandle<NeedShowJapanLogo>(this, "FFX.exe", 0x387450, h_need_show_japan_logo); // isNeedShowJapanLogo — suppresses Japan logo display during startup skip

        settings = new FhSettingsCategory("fhparry", [
            new FhSettingCustomRenderer("enabled", render_setting_enabled),
            new FhSettingCustomRenderer("difficulty", render_setting_difficulty),
            new FhSettingCustomRenderer("audio", render_setting_audio),
            new FhSettingCustomRenderer("audio_volume", render_setting_audio_volume),
            new FhSettingCustomRenderer("parry_state_hud", render_setting_parry_state_hud),
            new FhSettingCustomRenderer("startup_skip", render_setting_startup_skip),
            new FhSettingCustomRenderer("ctb", render_setting_overdrive_boost),
            new FhSettingCustomRenderer("logging", render_setting_logging),
            new FhSettingCustomRenderer("debug_overlay", render_setting_debug_overlay),
            new FhSettingCustomRenderer("negate", render_setting_negate),
            new FhSettingCustomRenderer("penalty", render_setting_penalty),
            new FhSettingCustomRenderer("future", render_setting_future)
        ]);
    }

    public override bool init(FhModContext mod_context, FileStream global_state_file)
    {
        _settingsFilePath = mod_context.Paths.SettingsPath;
        load_persistent_settings();
        initialize_session_logging(mod_context);
        _audioResourcesDir = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "audio");
        _fontResourcesDir = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "fonts");
        initialize_overlay_fonts();
        initialize_audio_resources();
        initialize_data_mappings(mod_context);

        try
        {
            _hMainLoop.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook Sg_MainLoop for simulation delta timing (falling back to fixed timestep): {ex.Message}");
        }

        try
        {
            _hMsExeInputCue.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsExeInputCue (continuing without native dispatch signal): {ex.Message}");
        }

        try
        {
            _hAtelEventSetUp.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook startup event setup (splash skip unavailable): {ex.Message}");
        }

        _startupLayerHooksEnabled = false;
        try_hook_startup_layer_calls();

        try
        {
            _hNeedShowJapanLogo.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook isNeedShowJapanLogo (startup logo skip reduced): {ex.Message}");
        }

        _logger.Info("ParryPrototype ready. Adjust options via Mod Config (F7).");
        return true;
    }

    public override void pre_update()
    {
        _debugFrameIndex++;
        float deltaSeconds = _timeSource.GetDeltaSeconds();
        _simulationClockSeconds += deltaSeconds;
        update_debug_save_loaded_state();
        try_run_startup_force_title_skip();
        try_apply_startup_warning_skip();
        try_apply_startup_movie_skip("pre_update");
        update_debug_battle_session_state();
        if (_optionDebugOverlay || _optionLogging)
        {
            monitor_cue_transitions();
        }

        if (!_optionEnabled)
        {
            reset_runtime_state("disabled", clearFeedbackFlashes: true, clearDamageFlags: true);
            process_turn_runtime_events();
            return;
        }

        bool hasEnemyCue = monitor_attack_cues();
        monitor_damage_resolves();
        update_parried_text_timer(deltaSeconds);
        advance_spam_penalty_timers(deltaSeconds);

        ParryInputContext parryInput = capture_parry_input_context();

        // Handle release first so if both release/press are visible in the same polling step,
        // we treat it as a tap-spam cycle and allow escalation.
        if (FhApi.Input.r1.just_released)
        {
            handle_parry_input_release(parryInput);
        }

        if (FhApi.Input.r1.just_pressed)
        {
            handle_parry_input_press(parryInput);
        }

        if (_runtime.ParryWindowActive)
        {
            _runtime.ParryWindowElapsedSeconds += deltaSeconds;
            _runtime.ParryWindowRemainingSeconds = MathF.Max(0f, _runtime.ParryWindowRemainingSeconds - deltaSeconds);
            if (_runtime.ParryWindowRemainingSeconds <= 0f)
            {
                end_parry_window("input_window_expired");
            }
        }
        else if (_runtime.AwaitingTurnEnd && !hasEnemyCue)
        {
            clear_awaiting_turn_end("Awaiting turn end cleared after no-cue update.");
        }

        validate_runtime_state();
        process_turn_runtime_events();
    }

    public override void render_imgui()
    {
        render_parry_state_hud();
        render_parry_window_overlay();
        render_debug_overlay();
    }

    private void reset_runtime_state(string timingReason, bool clearFeedbackFlashes, bool clearDamageFlags)
    {
        stop_audio_playback();
        _spamController.Reset("runtime_reset");

        _runtime.ParryWindowActive = false;
        _runtime.CurrentAttackerId = 0;
        _runtime.CurrentCueIndex = 0;
        _runtime.CurrentPartyTargetMask = 0;
        _runtime.CurrentCueSignature = 0;
        _runtime.ParryWindowRemainingSeconds = 0f;
        _runtime.AwaitingTurnEnd = false;
        _runtime.ParryWindowElapsedSeconds = 0f;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.LastDispatchConsumedFrame = 0;
        _runtime.LastDispatchConsumedAttackerId = 0;
        _runtime.LastDispatchConsumedQueueIndex = 0xFF;
        _runtime.LastCorrelationSkipFrame = 0;
        _runtime.CueFirstSeenFrame = 0;
        _runtime.WindowOpenFrame = 0;
        _runtime.WindowOpenTimestampSeconds = 0f;
        _impactCorrelationMatchedCount = 0;
        _impactCorrelationRejectedCount = 0;
        _impactCorrelationLastRejectReason = "None";
        _impactCorrelationLastSummaryFrame = 0;
        _impactCorrelationRejectCounts.Clear();

        if (clearFeedbackFlashes)
        {
            _runtime.ParriedTextRemainingSeconds = 0f;
            _runtime.ParryMissedTextRemainingSeconds = 0f;
            _runtime.LastParriedTargetSlot = -1;
        }

        if (clearDamageFlags)
        {
            Array.Clear(_damageEventActive);
        }
    }

    private void log_debug(string message)
    {
        bool appended = append_debug_event(message);

        if (_optionLogging && appended && !is_low_signal_log_message(message))
        {
            _logger.Info($"[Parry] {message}");
        }
    }

    private static bool is_low_signal_log_message(string message)
    {
        return message switch
        {
            "Parry input ignored (no parryable enemy cue)." => true,
            "Parry release ignored (no active parryable enemy cue)." => true,
            _ when message.StartsWith("Timeline integrity warning:", StringComparison.Ordinal) => true,
            _ => false
        };
    }

    private void h_main_loop_timing(float delta)
    {
        _timeSource.CaptureSimulationDelta(delta);
        _hMainLoop.orig_fptr(delta);
    }

    private DateTime current_gameplay_timestamp()
    {
        return DateTime.UnixEpoch + TimeSpan.FromSeconds(_simulationClockSeconds);
    }

    private double current_gameplay_seconds()
    {
        return _simulationClockSeconds;
    }

    private void validate_runtime_state()
    {
        _runtime.ParryWindowRemainingSeconds = MathF.Max(0f, _runtime.ParryWindowRemainingSeconds);
        _runtime.ParryWindowElapsedSeconds = MathF.Max(0f, _runtime.ParryWindowElapsedSeconds);
        _runtime.ParriedTextRemainingSeconds = MathF.Max(0f, _runtime.ParriedTextRemainingSeconds);
        _runtime.ParryMissedTextRemainingSeconds = MathF.Max(0f, _runtime.ParryMissedTextRemainingSeconds);

        if (!_runtime.ParryWindowActive && (_runtime.ParryWindowRemainingSeconds > 0f || _runtime.ParryWindowElapsedSeconds > 0f))
        {
            _runtime.ParryWindowRemainingSeconds = 0f;
            _runtime.ParryWindowElapsedSeconds = 0f;
        }

        if (!_runtime.AwaitingTurnEnd && _runtime.CurrentPartyTargetMask != 0)
        {
            _runtime.CurrentPartyTargetMask = 0;
        }
        if (!_runtime.AwaitingTurnEnd && _runtime.CurrentCueSignature != 0)
        {
            _runtime.CurrentCueSignature = 0;
        }
    }

    private void try_run_startup_force_title_skip()
    {
        if (!startup_skip_mutations_enabled())
        {
            return;
        }

        if (!_startupSkipStatusLogged)
        {
            _startupSkipStatusLogged = true;
            int startupEventId = *FhFfx.Globals.event_id;
            _logger.Info($"[Parry] Startup skip armed (option={_optionStartupSkipForceTitle}, event={startupEventId}).");
        }

        if (_battleAdapter.GetBattle() != null)
        {
            return;
        }

        if (_debugFrameIndex < 10)
        {
            return;
        }

        if (is_gameplay_ready_for_startup_skip())
        {
            return;
        }

        if (_simulationClockSeconds > StartupForceSkipWindowSeconds)
        {
            return;
        }

        int currentEventId = *FhFfx.Globals.event_id;
        string currentEventName = currentEventId > 0 ? get_current_event_name((uint)currentEventId) : string.Empty;
        bool isSplash = is_startup_splash_event((uint)Math.Max(0, currentEventId), currentEventName);
        bool isTitle = is_startup_title_event((uint)Math.Max(0, currentEventId), currentEventName);

        if (!isSplash && !isTitle)
        {
            return;
        }

        bool progressSet = try_set_startup_progress_flag("pre_update");
        if (!isSplash)
        {
            return;
        }

        if (_startupForceAttemptCount >= StartupForceMaxAttempts)
        {
            return;
        }

        if (_startupForceLastAttemptFrame != 0 && (_debugFrameIndex - _startupForceLastAttemptFrame) < StartupForceRetryFrames)
        {
            return;
        }

        bool redirected = false;
        try
        {
            // Re-apply redirect while splash events are active. This handles paths where startup re-enters memochek/loopdemo.
            _hAtelEventSetUp.orig_fptr(StartupSkipTitleRoomId);
            redirected = true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Startup redirect call failed: {ex.Message}");
        }

        _startupForceLastAttemptFrame = _debugFrameIndex;
        if (redirected)
        {
            _startupForceAttemptCount++;
        }

        if (progressSet || redirected || _startupForceAttemptCount <= 8 || (_startupForceAttemptCount % 10) == 0)
        {
            log_debug(
                $"Startup skip forced (event={currentEventId}, name={currentEventName}, progressSet={progressSet}, redirected={redirected}, attempts={_startupForceAttemptCount}).");
        }

        if (redirected || isTitle)
        {
        }
    }

    private void h_startup_event_setup(uint eventId)
    {
        if (_startupEventTraceCount < 20)
        {
            _startupEventTraceCount++;
            string traceName = get_current_event_name(eventId);
            _logger.Info($"[Parry] Startup event trace #{_startupEventTraceCount}: id={eventId}, name={traceName}.");
        }

        if (!startup_skip_mutations_enabled())
        {
            _hAtelEventSetUp.orig_fptr(eventId);
            return;
        }

        string eventName = get_current_event_name(eventId);
        if (is_startup_title_event(eventId, eventName))
        {
            try_set_startup_progress_flag("event:test20");
        }

        uint targetEventId = eventId;
        if (is_startup_splash_event(eventId, eventName))
        {
            try_set_startup_progress_flag($"event:{(string.IsNullOrWhiteSpace(eventName) ? eventId.ToString() : eventName)}");
            _logger.Info($"[Parry] Startup redirect: {(string.IsNullOrWhiteSpace(eventName) ? "event" : eventName)} ({eventId}) -> test20 ({StartupSkipTitleRoomId}).");
            targetEventId = StartupSkipTitleRoomId;
        }

        _hAtelEventSetUp.orig_fptr(targetEventId);

    }

    private void try_patch_startup_test20_script(string source)
    {
        if (!StartupTest20ScriptPatchEnabled)
        {
            return;
        }

        if (_startupTest20PatchApplied || !startup_skip_mutations_enabled())
        {
            return;
        }

        int eventId = *FhFfx.Globals.event_id;
        string eventName = eventId > 0 ? get_current_event_name((uint)eventId) : string.Empty;
        if (!is_startup_title_event((uint)Math.Max(0, eventId), eventName))
        {
            return;
        }

        Fahrenheit.Atel.AtelBasicWorker* worker = find_test20_patch_worker();
        if (worker == null)
        {
            return;
        }

        byte* code = worker->code_ptr;
        if (code == null)
        {
            return;
        }

        int patchedCount = 0;
        int alreadyPatched = 0;

        foreach (StartupScriptPatch patch in StartupTest20SplashPatches)
        {
            byte* target = code + patch.Offset;
            if (bytes_match(target, StartupPatchWait0Call))
            {
                alreadyPatched++;
                continue;
            }

            if (!bytes_match(target, patch.Expected))
            {
                if (!_startupTest20PatchMismatchLogged)
                {
                    _startupTest20PatchMismatchLogged = true;
                    _logger.Warning(
                        $"[Parry] Startup test20 patch aborted at {patch.Label} (offset=0x{patch.Offset:X4}): unexpected script bytes.");
                }
                return;
            }

            write_bytes(target, StartupPatchWait0Call);
            patchedCount++;
        }

        if (patchedCount > 0 || alreadyPatched == StartupTest20SplashPatches.Length)
        {
            _startupTest20PatchApplied = true;
            _logger.Info(
                $"[Parry] Startup test20 splash patch applied via {source} (patched={patchedCount}, already={alreadyPatched}).");
        }
    }

    private static Fahrenheit.Atel.AtelBasicWorker* find_test20_patch_worker()
    {
        try
        {
            Fahrenheit.Atel.AtelBasicWorker* currentWorker = FhFfx.Globals.Atel.current_worker;
            if (is_test20_patch_worker(currentWorker))
            {
                return currentWorker;
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            Fahrenheit.Atel.AtelWorkerController* controller = FhFfx.Globals.Atel.current_controller;
            if (controller == null)
            {
                return null;
            }

            int count = Math.Min(controller->runnable_script_count, (ushort)256);
            for (int i = 0; i < count; i++)
            {
                Fahrenheit.Atel.AtelBasicWorker* worker = (Fahrenheit.Atel.AtelBasicWorker*)controller->worker(i);
                if (is_test20_patch_worker(worker))
                {
                    return worker;
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static bool is_test20_patch_worker(Fahrenheit.Atel.AtelBasicWorker* worker)
    {
        if (worker == null || worker->script_chunk == null || worker->script_header == null)
        {
            return false;
        }

        if (worker->script_chunk->code_length < StartupTest20PatchRequiredCodeLength)
        {
            return false;
        }

        byte* code = worker->code_ptr;
        if (code == null)
        {
            return false;
        }

        foreach (StartupScriptPatch patch in StartupTest20SplashPatches)
        {
            byte* target = code + patch.Offset;
            if (!bytes_match(target, patch.Expected) && !bytes_match(target, StartupPatchWait0Call))
            {
                return false;
            }
        }

        return true;
    }

    private static bool bytes_match(byte* address, byte[] expected)
    {
        if (address == null || expected == null)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (address[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void write_bytes(byte* address, byte[] value)
    {
        if (address == null || value == null)
        {
            return;
        }

        for (int i = 0; i < value.Length; i++)
        {
            address[i] = value[i];
        }
    }

    private void try_hook_startup_common_calls()
    {
        if (_hCommonSetSplashSpriteExec != null
            || _hCommonSetSplashSpriteRetInt != null
            || _hCommonSetSplashSpriteRetFloat != null
            || _hCommonHideSplashExec != null
            || _hCommonHideSplashRetInt != null
            || _hCommonHideSplashRetFloat != null)
        {
            return;
        }

        try
        {
            Fahrenheit.Atel.AtelCallTarget* commonTargets = Fahrenheit.Atel.CTNamespaceExt.get_internal(Fahrenheit.Atel.AtelCallTargetNamespace.Common);
            if (commonTargets == null)
            {
                _logger.Warning("[Parry] Could not resolve Atel Common call table (startup probe hooks unavailable).");
                return;
            }

            Fahrenheit.Atel.AtelCallTarget setSplashSprite = commonTargets[0x011B];
            if (setSplashSprite.exec_func != 0)
            {
                _hCommonSetSplashSpriteExec = new FhMethodHandle<CommonSetSplashSpriteExec>(this, setSplashSprite.exec_func, h_common_set_splash_sprite_exec);
                _hCommonSetSplashSpriteExec.hook();
                _logger.Info($"[Parry] Startup probe hook armed (std::011B exec @ 0x{setSplashSprite.exec_func:X8}).");
            }

            if (setSplashSprite.ret_int_func != 0)
            {
                _hCommonSetSplashSpriteRetInt = new FhMethodHandle<CommonSetSplashSpriteRetInt>(this, setSplashSprite.ret_int_func, h_common_set_splash_sprite_ret_int);
                _hCommonSetSplashSpriteRetInt.hook();
                _logger.Info($"[Parry] Startup probe hook armed (std::011B ret_int @ 0x{setSplashSprite.ret_int_func:X8}).");
            }

            if (setSplashSprite.ret_float_func != 0)
            {
                _hCommonSetSplashSpriteRetFloat = new FhMethodHandle<CommonSetSplashSpriteRetFloat>(this, setSplashSprite.ret_float_func, h_common_set_splash_sprite_ret_float);
                _hCommonSetSplashSpriteRetFloat.hook();
                _logger.Info($"[Parry] Startup probe hook armed (std::011B ret_float @ 0x{setSplashSprite.ret_float_func:X8}).");
            }

            Fahrenheit.Atel.AtelCallTarget hideSplash = commonTargets[0x011C];
            if (hideSplash.exec_func != 0)
            {
                _hCommonHideSplashExec = new FhMethodHandle<CommonHideSplashExec>(this, hideSplash.exec_func, h_common_hide_splash_exec);
                _hCommonHideSplashExec.hook();
                _logger.Info($"[Parry] Startup probe hook armed (std::011C exec @ 0x{hideSplash.exec_func:X8}).");
            }

            if (hideSplash.ret_int_func != 0)
            {
                _hCommonHideSplashRetInt = new FhMethodHandle<CommonHideSplashRetInt>(this, hideSplash.ret_int_func, h_common_hide_splash_ret_int);
                _hCommonHideSplashRetInt.hook();
                _logger.Info($"[Parry] Startup probe hook armed (std::011C ret_int @ 0x{hideSplash.ret_int_func:X8}).");
            }

            if (hideSplash.ret_float_func != 0)
            {
                _hCommonHideSplashRetFloat = new FhMethodHandle<CommonHideSplashRetFloat>(this, hideSplash.ret_float_func, h_common_hide_splash_ret_float);
                _hCommonHideSplashRetFloat.hook();
                _logger.Info($"[Parry] Startup probe hook armed (std::011C ret_float @ 0x{hideSplash.ret_float_func:X8}).");
            }

            if (_hCommonSetSplashSpriteExec == null
                && _hCommonSetSplashSpriteRetInt == null
                && _hCommonSetSplashSpriteRetFloat == null
                && _hCommonHideSplashExec == null
                && _hCommonHideSplashRetInt == null
                && _hCommonHideSplashRetFloat == null)
            {
                _logger.Warning("[Parry] Startup probe hooks unresolved (std::011B/std::011C pointers are null).");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook std::011B/std::011C startup probe calls: {ex.Message}");
        }
    }

    private void h_common_set_splash_sprite_exec(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        trace_startup_common_call(0x011B, "setSplashSprite", "exec", argCount: 2, storage, stack);
        _hCommonSetSplashSpriteExec!.orig_fptr(work, storage, stack);
    }

    private int h_common_set_splash_sprite_ret_int(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        trace_startup_common_call(0x011B, "setSplashSprite", "ret_int", argCount: 2, storage, stack);
        return _hCommonSetSplashSpriteRetInt!.orig_fptr(work, storage, stack);
    }

    private float h_common_set_splash_sprite_ret_float(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        trace_startup_common_call(0x011B, "setSplashSprite", "ret_float", argCount: 2, storage, stack);
        return _hCommonSetSplashSpriteRetFloat!.orig_fptr(work, storage, stack);
    }

    private void h_common_hide_splash_exec(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        trace_startup_common_call(0x011C, "hideSplash", "exec", argCount: 1, storage, stack);
        _hCommonHideSplashExec!.orig_fptr(work, storage, stack);
    }

    private int h_common_hide_splash_ret_int(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        trace_startup_common_call(0x011C, "hideSplash", "ret_int", argCount: 1, storage, stack);
        return _hCommonHideSplashRetInt!.orig_fptr(work, storage, stack);
    }

    private float h_common_hide_splash_ret_float(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        trace_startup_common_call(0x011C, "hideSplash", "ret_float", argCount: 1, storage, stack);
        return _hCommonHideSplashRetFloat!.orig_fptr(work, storage, stack);
    }

    private void trace_startup_common_call(
        ushort callId,
        string callName,
        string source,
        int argCount,
        nint* storage,
        Fahrenheit.Atel.AtelStack* stack)
    {
        if (_startupCommonTraceCount >= 256)
        {
            return;
        }

        if (_simulationClockSeconds > 60.0f)
        {
            return;
        }

        if (_debugGameplayReady)
        {
            return;
        }

        int eventId = *FhFfx.Globals.event_id;
        string eventName = get_current_event_name_safe((uint)Math.Max(0, eventId));
        int arg0 = resolve_atel_call_arg(storage, stack, argCount, argIndex: 0);
        int arg1 = resolve_atel_call_arg(storage, stack, argCount, argIndex: 1);

        _startupCommonTraceCount++;
        append_debug_event(
            $"[Parry] Startup std::{callId:X4} {callName} [{source}] args=({format_probe_arg(arg0)}, {format_probe_arg(arg1)}) event={eventId}:{eventName}");
    }

    private static int resolve_atel_call_arg(nint* storage, Fahrenheit.Atel.AtelStack* stack, int argCount, int argIndex)
    {
        if (argIndex < 0 || argIndex >= argCount)
        {
            return int.MinValue;
        }

        try
        {
            if (stack != null && stack->size >= argCount)
            {
                int stackIndex = stack->size - argCount + argIndex;
                if (stackIndex >= 0 && stackIndex < stack->size)
                {
                    return stack->values.as_int()[stackIndex];
                }
            }
        }
        catch
        {
            // ignored; fall back to storage
        }

        try
        {
            if (storage != null && argIndex <= 3)
            {
                return unchecked((int)storage[argIndex]);
            }
        }
        catch
        {
            // ignored
        }

        return int.MinValue;
    }

    private static string format_probe_arg(int value)
    {
        return value == int.MinValue ? "-" : value.ToString(CultureInfo.InvariantCulture);
    }

    private void try_hook_startup_layer_calls()
    {
        if (!_startupLayerHooksEnabled)
        {
            return;
        }

        if (_hMapShow2DLayerExec != null || _hMapShow2DLayerRetInt != null || _hMapShow2DLayerRetFloat != null)
        {
            return;
        }

        try
        {
            Fahrenheit.Atel.AtelCallTarget* mapTargets = Fahrenheit.Atel.CTNamespaceExt.get_internal(Fahrenheit.Atel.AtelCallTargetNamespace.Map);
            if (mapTargets == null)
            {
                _logger.Warning("[Parry] Could not resolve Atel Map call table (startup 2D layer suppression unavailable).");
                return;
            }

            Fahrenheit.Atel.AtelCallTarget target = mapTargets[0x000F]; // map::800F (show2DLayer)
            if (target.exec_func == 0 && target.ret_int_func == 0 && target.ret_float_func == 0)
            {
                if (_startupLayerHookProbeTraceCount < 8)
                {
                    _startupLayerHookProbeTraceCount++;
                    _logger.Info("[Parry] map::800F unresolved yet (all pointers are null). Will retry.");
                }
                return;
            }

            if (target.exec_func != 0)
            {
                _hMapShow2DLayerExec = new FhMethodHandle<MapShow2DLayerExec>(this, target.exec_func, h_map_show_2d_layer_exec);
                _hMapShow2DLayerExec.hook();
                _logger.Info($"[Parry] Startup layer suppression hook armed (map::800F exec @ 0x{target.exec_func:X8}).");
                return;
            }

            if (target.ret_int_func != 0)
            {
                _hMapShow2DLayerRetInt = new FhMethodHandle<MapShow2DLayerRetInt>(this, target.ret_int_func, h_map_show_2d_layer_ret_int);
                _hMapShow2DLayerRetInt.hook();
                _logger.Info($"[Parry] Startup layer suppression hook armed (map::800F ret_int @ 0x{target.ret_int_func:X8}).");
                return;
            }

            if (target.ret_float_func != 0)
            {
                _hMapShow2DLayerRetFloat = new FhMethodHandle<MapShow2DLayerRetFloat>(this, target.ret_float_func, h_map_show_2d_layer_ret_float);
                _hMapShow2DLayerRetFloat.hook();
                _logger.Info($"[Parry] Startup layer suppression hook armed (map::800F ret_float @ 0x{target.ret_float_func:X8}).");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook map::800F startup layer call: {ex.Message}");
        }
    }

    private void h_map_show_2d_layer_exec(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        int layerIndex = resolve_map_layer_index(storage, stack);
        trace_map_show_2d_layer_args("exec", layerIndex, storage, stack);
        if (should_skip_startup_layer_show(layerIndex, "exec"))
        {
            return;
        }
        _hMapShow2DLayerExec!.orig_fptr(work, storage, stack);
    }

    private int h_map_show_2d_layer_ret_int(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        int layerIndex = resolve_map_layer_index(storage, stack);
        trace_map_show_2d_layer_args("ret_int", layerIndex, storage, stack);
        if (should_skip_startup_layer_show(layerIndex, "ret_int"))
        {
            return 0;
        }
        int result = _hMapShow2DLayerRetInt!.orig_fptr(work, storage, stack);
        return result;
    }

    private float h_map_show_2d_layer_ret_float(Fahrenheit.Atel.AtelBasicWorker* work, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        int layerIndex = resolve_map_layer_index(storage, stack);
        trace_map_show_2d_layer_args("ret_float", layerIndex, storage, stack);
        if (should_skip_startup_layer_show(layerIndex, "ret_float"))
        {
            return 0f;
        }
        float result = _hMapShow2DLayerRetFloat!.orig_fptr(work, storage, stack);
        return result;
    }

    private static int resolve_map_layer_index(nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        // For map::800F in our startup path, the incoming layer argument is observed on stack top.
        // storage[0] can hold the return slot/current result and often appears as 0.
        try
        {
            if (stack != null && stack->size > 0)
            {
                int i = stack->size - 1;
                int candidate = stack->values.as_int()[i];
                if (candidate >= 0 && candidate <= 64) return candidate;
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            if (storage != null)
            {
                int s1 = unchecked((int)storage[1]);
                if (s1 >= 0 && s1 <= 64) return s1;
                int s0 = unchecked((int)storage[0]);
                if (s0 >= 0 && s0 <= 64) return s0;
            }
        }
        catch
        {
            // ignored, fallback below
        }

        return -1;
    }

    private void trace_map_show_2d_layer_args(string source, int resolvedLayer, nint* storage, Fahrenheit.Atel.AtelStack* stack)
    {
        if (_startupLayerArgTraceCount >= 32)
        {
            return;
        }

        if (_simulationClockSeconds > StartupLayerSuppressWindowSeconds)
        {
            return;
        }

        int eventId = *FhFfx.Globals.event_id;
        string eventName = eventId > 0 ? get_current_event_name((uint)eventId) : string.Empty;
        if (!is_startup_title_event((uint)Math.Max(0, eventId), eventName)
            && !is_startup_splash_event((uint)Math.Max(0, eventId), eventName))
        {
            return;
        }

        int s0 = int.MinValue;
        int s1 = int.MinValue;
        int stackTop = int.MinValue;
        int stackSize = -1;

        try
        {
            if (storage != null)
            {
                s0 = unchecked((int)storage[0]);
                s1 = unchecked((int)storage[1]);
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            if (stack != null)
            {
                stackSize = stack->size;
                if (stackSize > 0)
                {
                    stackTop = stack->values.as_int()[stackSize - 1];
                }
            }
        }
        catch
        {
            // ignored
        }

        _startupLayerArgTraceCount++;
        _logger.Info(
            $"[Parry] map::800F arg trace #{_startupLayerArgTraceCount}: src={source}, resolved={resolvedLayer}, s0={s0}, s1={s1}, stackSize={stackSize}, stackTop={stackTop}, event={eventId}, name={eventName}.");
    }

    private bool should_skip_startup_layer_show(
        int layerIndex,
        string source)
    {
        if (!startup_skip_mutations_enabled()) return false;
        if (!should_suppress_startup_layer(layerIndex)) return false;
        if (_startupHideLastFrame == _debugFrameIndex && _startupHideLastLayer == layerIndex) return true;

        _startupHideLastFrame = _debugFrameIndex;
        _startupHideLastLayer = layerIndex;

        if (_startupLayerTraceCount < 24)
        {
            _startupLayerTraceCount++;
            int eventId = *FhFfx.Globals.event_id;
            string eventName = eventId > 0 ? get_current_event_name((uint)eventId) : string.Empty;
            _logger.Info($"[Parry] Suppressed startup layer {layerIndex} at map::800F ({source}; event={eventId}, name={eventName}, t={_simulationClockSeconds:F2}s).");
        }

        return true;
    }

    private bool should_suppress_startup_layer(int layerIndex)
    {
        if (!_optionStartupSkipForceTitle)
        {
            return false;
        }

        if (_simulationClockSeconds > StartupLayerSuppressWindowSeconds)
        {
            return false;
        }

        int eventId = *FhFfx.Globals.event_id;
        if (eventId <= 0)
        {
            return false;
        }

        string eventName = get_current_event_name((uint)eventId);
        if (!is_startup_title_event((uint)eventId, eventName)
            && !is_startup_splash_event((uint)eventId, eventName))
        {
            return false;
        }

        if (layerIndex < 0)
        {
            return false;
        }

        // Keep layer suppression scoped to known secondary overlays.
        // Primary layer 13 suppression caused startup hangs in live runs.
        if (layerIndex != StartupLayerSecondaryAId
            && layerIndex != StartupLayerSecondaryBId)
        {
            return false;
        }

        return true;
    }

    private int h_need_show_japan_logo()
    {
        if (startup_skip_mutations_enabled() && !is_gameplay_ready_for_startup_skip())
        {
            return 0;
        }

        return _hNeedShowJapanLogo.orig_fptr();
    }

    private void try_apply_startup_warning_skip()
    {
        if (!startup_skip_mutations_enabled())
        {
            return;
        }

        if (is_gameplay_ready_for_startup_skip())
        {
            return;
        }

        uint* gNeedAutoSave = FhFfx.FhCall.gNeedAutoSave;
        if (gNeedAutoSave == null)
        {
            return;
        }

        byte current = (byte)(*gNeedAutoSave & 0xFF);
        if (!_startupNeedAutoSaveLogged || current != _startupNeedAutoSaveLast)
        {
            _startupNeedAutoSaveLogged = true;
            _startupNeedAutoSaveLast = current;
            _logger.Info($"[Parry] Startup gNeedAutoSave={current}.");
        }

        if (current != 0)
        {
            *gNeedAutoSave = 0;
            _startupNeedAutoSaveLast = 0;
            _logger.Info("[Parry] Startup gNeedAutoSave forced to 0.");
        }
    }

    private bool is_gameplay_ready_for_startup_skip()
    {
        int eventId = *FhFfx.Globals.event_id;
        if (eventId <= 0)
        {
            return false;
        }

        string eventName = get_current_event_name((uint)eventId);
        if (!is_startup_title_event((uint)eventId, eventName)
            && !is_startup_splash_event((uint)eventId, eventName))
        {
            return true;
        }

        byte* menuState = FhUtil.ptr_at<byte>(0xF407E4);
        return menuState != null && *menuState != 0;
    }

    private void try_apply_startup_movie_skip(string source)
    {
        if (_startupMovieSkipApplied || !startup_skip_mutations_enabled())
        {
            return;
        }

        if (is_gameplay_ready_for_startup_skip())
        {
            return;
        }

        if (_simulationClockSeconds > StartupForceSkipWindowSeconds)
        {
            return;
        }

        try
        {
            uint* gMoviePlay = FhUtil.ptr_at<uint>(0xD2A008);
            if (gMoviePlay != null)
            {
                *gMoviePlay = 0;
            }

            FhUtil.get_fptr<MovieStopProg>(0x36F1A0)();
            _startupMovieSkipApplied = true;
            int eventId = *FhFfx.Globals.event_id;
            _logger.Info($"[Parry] Startup movie skip applied via {source} (event={eventId}).");
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Startup movie skip failed: {ex.Message}");
        }
    }

    private static bool is_startup_title_event(uint eventId, string eventName)
    {
        return eventId == StartupSkipTitleRoomId
            || string.Equals(eventName, "test20", StringComparison.OrdinalIgnoreCase);
    }

    private static bool is_startup_splash_event(uint eventId, string eventName)
    {
        return eventId == StartupSkipMemochekEventId
            || eventId == StartupSkipLoopdemoEventId
            || string.Equals(eventName, "memochek", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventName, "loopdemo", StringComparison.OrdinalIgnoreCase);
    }

    private bool startup_skip_mutations_enabled()
    {
        return _optionStartupSkipForceTitle;
    }

    private bool try_set_startup_progress_flag(string source)
    {
        FhFfx.SaveData* save = FhFfx.Globals.save_data;
        if (save == null)
        {
            return false;
        }

        byte* raw = (byte*)save;
        byte current = raw[StartupSkipProgressFlagOffset];
        if (current == 1)
        {
            return true;
        }

        raw[StartupSkipProgressFlagOffset] = 1;
        _logger.Info($"[Parry] Startup flag set via {source}: saveData0C88 {current} -> 1.");
        return true;
    }

    private static string get_current_event_name(uint eventId)
    {
        try
        {
            char* ptr = FhUtil.get_fptr<AtelGetEventName>(0x4796e0)(eventId);
            if (ptr == null) return string.Empty;
            return Marshal.PtrToStringAnsi((nint)ptr) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class DebugLogEntry
    {
        public DateTime TimestampLocal { get; set; }
        public double SimulationSeconds { get; set; }
        public ulong FrameIndex { get; set; }
        public string Message { get; set; } = string.Empty;
        public int RepeatCount { get; set; } = 1;
    }
}
