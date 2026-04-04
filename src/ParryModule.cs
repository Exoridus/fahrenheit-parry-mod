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
    private const float ParryWindowBackstopSeconds = 10f;
    private const int DebugLogRingCapacity = 500;
    private const int CueHistoryRingCapacity = 64;
    private const int DebugTurnRowCapacity = 500;
    private const ushort StartupSkipTitleRoomId = 23;
    private const uint StartupSkipMemochekEventId = 348;
    private const uint StartupSkipLoopdemoEventId = 349;
    private const int StartupSkipProgressFlagOffset = 0xC88;
    private const float StartupForceSkipWindowSeconds = 20.0f;
    private const int StartupTest20PatchRequiredCodeLength = 0x381;
    private const int StartupForceRetryFrames = 3;
    private const int StartupForceMaxAttempts = 120;
    private const float StartupProbeWindowSeconds = 30.0f;
    private const int StartupProbePeriodicFrames = 5;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AtelEventSetUp(uint eventId);
    private delegate char* AtelGetEventName(uint eventId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NeedShowJapanLogo();
    // Community-confirmed signature: int __cdecl MsSetDamage(byte param_1, int param_2, int param_3)
    // param_1 = attacker battler slot
    // param_2 = target party slot (>= 0) for the actual damage call, or -5 for setup/finalization
    // param_3 = 0 for setup/target calls, 0x400 (1024) for finalization (triggers MsAfterDamageProcess)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsSetDamageProbe(byte param_1, int param_2, int param_3);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsDamageSetMotionProbe(byte target, int p2, int p3);
    // Community-confirmed signature for MsCalcDamage (March 2026 Discord findings).
    // Pointer params use nint — hook does not dereference them.
    // p11 = hit count per target.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsCalcDamageProbe(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DmgCalcArmoredProbe(Chr* user, Chr* target, Command* command, int p4, int* p5, int damage);

    // Community name: MsCalcDamageInternal / FUN_0078e680 at FFX.exe+0x38e680.
    // Inner per-hit damage calculator called from within MsCalcDamage.
    // Ghidra signature: 11 params matching MsCalcDamage forwarding shape.
    // Active interception hook: returns 0 (skipping orig) when a parry expiry is active,
    // preventing both damage_hp and native DamageInfo buffer writes per hit.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsCalcDamageInternalProbe(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11);

    // FUN_0078f0b0 at FFX.exe+0x38F0B0 — per-target native commit point.
    // Internally calls MsSubHP → MsDamageCheckDeath → MsDamageSetMotion for each targeted slot.
    // Returning early atomically prevents HP reduction, death-latch, status commit, and flinch.
    // Phase 1 reactive hook: returns early when _parryExpiry[param_3] is active for the target slot.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsSetDamageInternalProbe(int param_1, byte param_2, int param_3, int param_4, int param_5);

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

    /// <summary>
    ///     Per-slot telemetry record for one attack turn.
    ///     Reset after flush at turn end. Written unconditionally; flush output is gated on _optionLogging.
    /// </summary>
    private struct AttackTelemetry
    {
        public bool CalcDamageFired;
        public bool CalcDamageIntercepted;
        public bool SetMotionFired;        // MsDamageSetMotion p2=5 (party flinch)
        public bool SetDamageTargetFired;  // MsSetDamage p2=target
        public uint HpBeforeFinalization;
        public uint HpAfterFinalization;
        public int CommandId;
    }

    private readonly struct StartupScriptPatch
    {
        public readonly int Offset;
        public readonly string Label;
        public readonly byte[] Expected;
        public readonly byte[] Payload;

        public StartupScriptPatch(int offset, string label, byte[] payload, byte[] expected)
        {
            Offset = offset;
            Label = label;
            Payload = payload;
            Expected = expected;
        }
    }

    private static readonly StartupScriptPatch[] StartupTest20SplashPatches = new StartupScriptPatch[] {
        // Overwrites the Autosave-Check with "Jump to j09 (Offset 02B7)" -> Skips Room 348 entirely
        new StartupScriptPatch(0x0289, "skip-autosave", 
            new byte[] { 0xB0, 0x09, 0x00, 0x3C, 0x3C, 0x3C, 0x3C, 0x3C, 0x3C, 0x3C }, 
            expected: new byte[] { 0x9F, 0x01, 0x00, 0xAE, 0x00, 0x00, 0x06, 0xD7, 0x09, 0x00 }),
        
        // Overwrites an overlay clear with "Jump to j12 (Offset 0397)" -> Skips FF Logo & Video, jumps to Menu
        new StartupScriptPatch(0x02C6, "skip-ff-logo-and-movie", 
            new byte[] { 0xB0, 0x12, 0x00 }, 
            expected: new byte[] { 0xD8, 0x0C, 0x40 })
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
        public uint LastParriedTargetMask;
        public ulong LastDispatchConsumedFrame;
        public byte LastDispatchConsumedAttackerId;
        public byte LastDispatchConsumedQueueIndex;
        public ulong LastCorrelationSkipFrame;

        public ulong CueFirstSeenFrame;
        public ulong WindowOpenFrame;
        public float WindowOpenTimestampSeconds;
        public float WindowDurationSecondsAtOpen;

        // Set when an impact is detected for this turn but the parry window was not open.
        // Prevents the Anfunkeln finalization fallback from resolving as PARRIED when the
        // player opens the window after damage was already dealt and the poll missed it.
        public bool TurnImpactMissedSeen;
        // Attacker ID recorded when TurnImpactMissedSeen was set. Used to avoid clearing
        // the flag when the window opens for the same attacker that already dealt damage.
        public byte TurnImpactMissedAttackerId;

        // Status-block display: shown where "Parried" would appear when a hit is silently
        // skipped because the target's battle status prevents them from being parried.
        public float StatusBlockTextRemainingSeconds;
        public string StatusBlockLabel;

        public static ParryRuntimeState CreateDefault() => new()
        {
            LastParriedTargetMask = 0,
            LastDispatchConsumedQueueIndex = 0xFF,
            StatusBlockLabel = string.Empty
        };
    }

    private bool _optionEnabled = true;
    private bool _optionSound = true;
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
    private ParryDifficulty _optionDifficulty = ParryDifficulty.Easy;
    private readonly bool[] _damageEventActive = new bool[PartyActorCapacity];
    private readonly bool[] _parryFeedbackPending = new bool[PartyActorCapacity];
    // Per-turn bitmask of slots intercepted at MsSetDamageInternal. Prevents double-resolution
    // when both skipOrigForParry (h_ms_set_damage) and the internal hook could both fire.
    private uint _internalInterceptedMask;
    // Per-turn attacker id recorded at p5=0 intercept time. Prevents a different attacker's
    // p5=1024 from inheriting the skip when the parry window closes between the two passes.
    private readonly byte[] _internalInterceptedAttackerId = new byte[PartyActorCapacity];
    // Durable per-turn marker: set at MsDamageSetMotion (visual impact time) when the parry
    // timing gate passed. Consumed at MsSetDamageInternal p5=1024 (authoritative HP/death commit)
    // to skip the commit without re-evaluating the wall-clock window, which may have expired
    // by the time the delayed commit pass fires (Anfunkeln, Blitzra, multi-pass attacks).
    private uint _parryResolvedAtImpactMask;
    private readonly long[] _parryExpiry = new long[PartyActorCapacity];
    private readonly byte[] _parryArmedAttackerId = new byte[PartyActorCapacity];
    private readonly uint[] _preHitHpSnapshot = new uint[PartyActorCapacity];
    private readonly AttackTelemetry[] _attackTelemetry = new AttackTelemetry[PartyActorCapacity];
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
    private ulong _msSetDamageLogLastFrame;
    private byte _msSetDamageLogLastP1;
    private int _msSetDamageLogLastP2;
    private int _msSetDamageLogLastP3;
    private int _msSetDamageLogLastResult;
    private bool _msSetDamageLogLastParryWindowActive;
    private byte _msSetDamageLogLastAttackerId;
    private bool _msSetDamageLogLastAwaitingTurnEnd;
    private ulong _msCalcDamageLogLastFrame;
    private int _msCalcDamageLogLastUserId;
    private int _msCalcDamageLogLastTargetId;
    private int _msCalcDamageLogLastCommandId;
    private int _msCalcDamageLogLastHitCount;
    private int _msCalcDamageLogLastResult;
    private double _simulationClockSeconds;
    private ulong _debugFrameIndex;
    private ulong _debugBattleFrameIndex;
    private bool _debugBattleActive;
    private bool _debugBattleSessionFirstCueSeen;
    private bool _debugGameSaveLoaded;
    private bool _debugGameplayReady;
    private bool _startupSkipStatusLogged;
    private int _startupForceAttemptCount;
    private ulong _startupForceLastAttemptFrame;
    private int _startupEventTraceCount;
    private bool _startupTest20PatchApplied;
    private bool _startupTest20PatchMismatchLogged;
    private bool _debugAutoScroll = true;
    private bool _debugCueAutoScroll = true;
    private float _debugCuePanelRatio = 0.50f;
    private int _debugCueTurnId;
    private string _dataMappingStatus = "No data mappings loaded.";
    private readonly Random _rng = new();
    private string _settingsFilePath = string.Empty;
    private StreamWriter? _sessionDebugLogWriter;
    private StreamWriter? _sessionTimelineLogWriter;
    private StreamWriter? _sessionStartupProbeWriter;
    private string _sessionLogsRoot = string.Empty;
    private string _sessionLogDirectory = string.Empty;
    private string _sessionLogPrefix = string.Empty;
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
    private readonly FhMethodHandle<FhFfx.FhCall.MsExeInputCue> _hMsExeInputCue;
    private readonly FhMethodHandle<MsSetDamageProbe> _hMsSetDamage;
    private readonly FhMethodHandle<MsDamageSetMotionProbe> _hMsDamageSetMotion;
    private readonly FhMethodHandle<MsCalcDamageProbe> _hMsCalcDamage;
    private readonly FhMethodHandle<DmgCalcArmoredProbe> _hDmgCalcArmored;
    private readonly FhMethodHandle<MsCalcDamageInternalProbe> _hMsCalcDamageInternal;
    private readonly FhMethodHandle<MsSetDamageInternalProbe> _hMsSetDamageInternal;
    private readonly FhMethodHandle<AtelEventSetUp> _hAtelEventSetUp;
    private readonly FhMethodHandle<NeedShowJapanLogo> _hNeedShowJapanLogo;

    public ParryModule()
    {
        _hMsExeInputCue = new FhMethodHandle<FhFfx.FhCall.MsExeInputCue>(this, "FFX.exe", FhFfx.FhCall.__addr_MsExeInputCue, h_ms_exe_input_cue);
        _hMsSetDamage = new FhMethodHandle<MsSetDamageProbe>(this, "FFX.exe", FhFfx.FhCall.__addr_MsSetDamage, h_ms_set_damage);
        _hMsDamageSetMotion = new FhMethodHandle<MsDamageSetMotionProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsDamageSetMotion, h_ms_damage_set_motion);
        _hMsCalcDamage = new FhMethodHandle<MsCalcDamageProbe>(this, "FFX.exe", FhFfx.FhCall.__addr_MsCalcDamage, h_ms_calc_damage);
        _hDmgCalcArmored = new FhMethodHandle<DmgCalcArmoredProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.DmgCalcArmored, h_dmg_calc_armored);
        _hMsCalcDamageInternal = new FhMethodHandle<MsCalcDamageInternalProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsCalcDamageInternal, h_ms_calc_damage_internal);
        _hMsSetDamageInternal = new FhMethodHandle<MsSetDamageInternalProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.DiscordCandidates.FnMsSetDamageInternal, h_ms_set_damage_internal);
        _hAtelEventSetUp = new FhMethodHandle<AtelEventSetUp>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.AtelEventSetUp, h_startup_event_setup); // AtelEventSetUp — Atel scripting event dispatch; intercepted for startup skip
        _hNeedShowJapanLogo = new FhMethodHandle<NeedShowJapanLogo>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.NeedShowJapanLogo, h_need_show_japan_logo); // isNeedShowJapanLogo — suppresses Japan logo display during startup skip

        settings = new FhSettingsCategory("fhparry", [
            new FhSettingCustomRenderer("enabled", render_setting_enabled),
            new FhSettingCustomRenderer("difficulty", render_setting_difficulty),
            new FhSettingCustomRenderer("audio", render_setting_audio),
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
        warmup_audio_playback_once();
        initialize_data_mappings(mod_context);

        FhApi.Events.Common.GameLoop.PreUpdate.subscribe(on_pre_update);

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
            _hMsSetDamage.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsSetDamage (experimental spike inactive): {ex.Message}");
        }

        try
        {
            _hMsDamageSetMotion.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsDamageSetMotion: {ex.Message}");
        }

        try
        {
            _hDmgCalcArmored.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook DmgCalcArmored Probe: {ex.Message}");
        }

        try
        {
            _hMsCalcDamageInternal.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsCalcDamageInternal Probe: {ex.Message}");
        }

        try
        {
            _hMsSetDamageInternal.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsSetDamageInternal Probe: {ex.Message}");
        }

        try
        {
            _hMsCalcDamage.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsCalcDamage (hit-count probe inactive): {ex.Message}");
        }

        try
        {
            _hAtelEventSetUp.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook startup event setup (splash skip unavailable): {ex.Message}");
        }

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

    private void on_pre_update(Fahrenheit.Events.UpdateLoopEventArgs e)
    {
        _debugFrameIndex++;
        float deltaSeconds = e.delta;
        _simulationClockSeconds += deltaSeconds;
        update_debug_save_loaded_state();
        try_run_startup_force_title_skip();
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

        // Poll damage after input so that a pre-held R1 window (armed inside
        // monitor_attack_cues on cue identity change) is visible here.
        monitor_damage_resolves();

        if (_runtime.ParryWindowActive)
        {
            // Window stays open until the attack resolves (cue clears via clear_awaiting_turn_end
            // or damage lands via on_impact_detected). Track elapsed time for telemetry only.
            _runtime.ParryWindowElapsedSeconds += deltaSeconds;

            _runtime.ParryWindowRemainingSeconds -= deltaSeconds;
            if (_runtime.ParryWindowRemainingSeconds <= 0f)
            {
                log_debug($"Parry window expired ({format_actor_slot(_runtime.CurrentAttackerId)}, no hit).");
                end_parry_window("backstop_expired");
            }
        }

        // Cue-cleared cleanup must run regardless of window state. If the cue disappears while
        // the window is still open (e.g., attack animation completed without a damage event),
        // clear_awaiting_turn_end also closes the window to prevent it staying open permanently.
        if (_runtime.AwaitingTurnEnd && !hasEnemyCue)
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
        _runtime.WindowDurationSecondsAtOpen = 0f;
        _impactCorrelationMatchedCount = 0;
        _impactCorrelationRejectedCount = 0;
        _impactCorrelationLastRejectReason = "None";
        _impactCorrelationLastSummaryFrame = 0;
        _impactCorrelationRejectCounts.Clear();

        if (clearFeedbackFlashes)
        {
            _runtime.ParriedTextRemainingSeconds = 0f;
            _runtime.ParryMissedTextRemainingSeconds = 0f;
            _runtime.StatusBlockTextRemainingSeconds = 0f;
            _runtime.StatusBlockLabel = string.Empty;
            _runtime.LastParriedTargetMask = 0;
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
        _runtime.StatusBlockTextRemainingSeconds = MathF.Max(0f, _runtime.StatusBlockTextRemainingSeconds);

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
            try_patch_startup_test20_script("pre_update");
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

        if (is_startup_title_event(targetEventId, eventName))
        {
            try_patch_startup_test20_script("event_setup");
        }
    }

    private void try_patch_startup_test20_script(string source)
    {
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
            if (bytes_match(target, patch.Payload))
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

            write_bytes(target, patch.Payload);
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
            if (!bytes_match(target, patch.Expected) && !bytes_match(target, patch.Payload))
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

    private int h_need_show_japan_logo()
    {
        if (startup_skip_mutations_enabled() && !is_gameplay_ready_for_startup_skip())
        {
            return 0;
        }

        return _hNeedShowJapanLogo.orig_fptr();
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

        byte* menuState = FhUtil.ptr_at<byte>(ExternalMemoryOffsetMap.StartupState.MenuState);
        return menuState != null && *menuState != 0;
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
            char* ptr = FhUtil.get_fptr<AtelGetEventName>(ExternalMemoryOffsetMap.Functions.AtelGetEventName)(eventId);
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
