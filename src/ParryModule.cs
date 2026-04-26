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
    private const float StartupForceSkipWindowSeconds = 20.0f;
    private const int StartupTest20PatchRequiredCodeLength = 0x381;
    private const int StartupAutosavePatchOffset = 0x0289;
    private const uint StartupMaxSafeCodeLength = 0x20000; // Hard safety cap for startup script reads/scans.
    private const uint StartupMaxSafeCodeOffset = 0x100000;
    private const int StartupControllerScanLimit = 16;
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

    private enum StartupCodeSource
    {
        None,
        CurrentWorker,
        CurrentControllerWorker0,
        CurrentController,
        ControllersArrayWorker0,
        ControllersArray
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    private static readonly StartupScriptPatch[] StartupTest20SplashPatches = new StartupScriptPatch[] {
        // Overwrites the Autosave-Check with "Jump to j09 (Offset 02B7)" -> Skips Room 348 entirely.
        new StartupScriptPatch(StartupAutosavePatchOffset, "skip-autosave",
            new byte[] { 0xB0, 0x09, 0x00, 0x3C, 0x3C, 0x3C, 0x3C, 0x3C, 0x3C, 0x3C },
            expected: new byte[] { 0x9F, 0x01, 0x00, 0xAE, 0x00, 0x00, 0x06, 0xD7, 0x09, 0x00 }),

        // Overwrites an overlay clear with "Jump to j0D (Offset 032A)" -> Skips FF Logo & Video, lands before show2DLayer(13)->j0C->j12
        // j0D directly calls show2DLayer(13) then jumps to j0C, which selects layer 2/4 by region before fading in.
        // Previously targeted j12 (0397) which skipped all show2DLayer calls, leaving the title background invisible.
        new StartupScriptPatch(0x02C6, "skip-ff-logo-and-movie",
            new byte[] { 0xB0, 0x0D, 0x00 },
            expected: new byte[] { 0xD8, 0x0C, 0x40 })
    };

    // Runtime-only mutable state lives here to keep transitions centralized and auditable.
    private struct ParryRuntimeState
    {
        // Canonical press-based parry state machine (FINAL_PARRY_SPEC.md).
        // All R1-press gating goes through InputState. ParryWindowActive is a legacy
        // convenience mirror kept true while InputState == Open.
        public ParryInputState InputState;
        public bool ParryWindowActive;
        public byte CurrentAttackerId;
        public byte CurrentCueIndex;
        public uint CurrentPartyTargetMask;
        public uint CurrentCueSignature;
        public float ParryWindowRemainingSeconds;
        // Whiff recovery lockout: countdown that approximates the "return to normal
        // stance" animation commitment. Non-zero only while InputState == WhiffLockout.
        public float WhiffLockoutRemainingSeconds;
        public float WhiffLockoutTotalSeconds;
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
            InputState = ParryInputState.Ready,
            LastParriedTargetMask = 0,
            LastDispatchConsumedQueueIndex = 0xFF,
            StatusBlockLabel = string.Empty
        };
    }

    private bool _optionEnabled = true;
    private bool _optionSound = true;
    private bool _optionLogging =
#if DEBUG
        true;
#else
        false;
#endif
    private bool _optionParryStateHud = true;
    private bool _optionOverdriveBoost = true;
    private bool _optionNegateDamage = true;
    // Enables the animation-approximated whiff recovery lockout. When disabled, a
    // whiffed window transitions straight back to Ready with no commitment penalty.
    // Persisted as "penalty" for settings backward compatibility; see
    // TIERED_PENALTY_RATIONALE.md (retired) for the historical name.
    private bool _optionWhiffLockout = true;
    // Native-engine probe channel. When false (default), probe queue is inert
    // and no native-probe events are recorded. When true, probe-tagged events
    // are pushed onto _probeRingBuffer during hook execution and drained once
    // per pre-update tick into the session debug log. Independent of
    // _optionLogging — enabling probes does NOT enable other logging, and
    // turning logging on does NOT enable probes.
    //
    // No production hook currently emits probe events. The wiring is in place
    // ahead of Stage-1 observe probes per the KB probe plan.
    private bool _optionNativeProbeLogging = false;
    private bool _optionStartupSkipForceTitle = true;
    private bool _optionStartupProbeMode = false;
    private bool _optionDebugOverlay =
#if DEBUG
        true;
#else
        false;
#endif
    private ParryDifficulty _optionDifficulty = ParryDifficultyModel.DefaultDifficulty;
    private readonly bool[] _damageEventActive = new bool[PartyActorCapacity];
    private readonly bool[] _parryFeedbackPending = new bool[PartyActorCapacity];
    // Per-hit bitmask of slots intercepted at MsSetDamageInternal. Set at p5=0 and
    // consumed/cleared at that slot's p5=1024 completion boundary.
    private uint _internalInterceptedMask;
    // Per-hit attacker id recorded at p5=0 intercept time for slot-correlated p5=1024 skip.
    private readonly byte[] _internalInterceptedAttackerId = new byte[PartyActorCapacity];
    // Slot-local marker: p5=0 already committed before a valid active parry gate existed.
    // Used to prevent late false-positive "parry success" promotion while still allowing
    // duplicate native commit passes to be safely skipped.
    private uint _latePreOpenP5ZeroCommitMask;
    private readonly byte[] _latePreOpenP5ZeroCommitAttackerId = new byte[PartyActorCapacity];
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
    private bool _startupDiagResolveFailureLogged;
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

        ParryInputContext parryInput = capture_parry_input_context();

        if (FhApi.Input.r1.just_pressed)
        {
            handle_parry_input_press(parryInput);
        }

        // Poll damage after input so the open window set by the press is visible.
        monitor_damage_resolves();

        // Parry input state machine tick (FINAL_PARRY_SPEC.md).
        //
        //   Open          -> WhiffLockout  on window expiry without a hit
        //   WhiffLockout  -> Ready         when recovery timer elapses
        //
        // Resolved -> Ready is handled when the cue clears or the hit finalizes
        // (see clear_awaiting_turn_end / end_parry_window).
        if (_runtime.InputState == ParryInputState.Open && _runtime.ParryWindowActive)
        {
            _runtime.ParryWindowElapsedSeconds += deltaSeconds;
            _runtime.ParryWindowRemainingSeconds -= deltaSeconds;
            if (_runtime.ParryWindowRemainingSeconds <= 0f)
            {
                transition_to_whiff_lockout();
            }
        }
        else if (_runtime.InputState == ParryInputState.WhiffLockout)
        {
            _runtime.WhiffLockoutRemainingSeconds -= deltaSeconds;
            if (_runtime.WhiffLockoutRemainingSeconds <= 0f)
            {
                transition_whiff_lockout_to_ready();
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

        // Drain any deferred native-probe events queued by hooks during this
        // frame. No-op when _optionNativeProbeLogging is false; the ring is
        // empty and the early-return inside drain_probe_ring exits cheaply.
        drain_probe_ring();
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

        _runtime.InputState = ParryInputState.Ready;
        _runtime.ParryWindowActive = false;
        _runtime.CurrentAttackerId = 0;
        _runtime.CurrentCueIndex = 0;
        _runtime.CurrentPartyTargetMask = 0;
        _runtime.CurrentCueSignature = 0;
        _runtime.ParryWindowRemainingSeconds = 0f;
        _runtime.WhiffLockoutRemainingSeconds = 0f;
        _runtime.WhiffLockoutTotalSeconds = 0f;
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

    private bool should_capture_debug_messages()
    {
        return _optionLogging || _optionDebugOverlay;
    }

    private void log_debug(string message)
    {
        if (!should_capture_debug_messages())
        {
            return;
        }

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
        _runtime.WhiffLockoutRemainingSeconds = MathF.Max(0f, _runtime.WhiffLockoutRemainingSeconds);
        _runtime.ParriedTextRemainingSeconds = MathF.Max(0f, _runtime.ParriedTextRemainingSeconds);
        _runtime.ParryMissedTextRemainingSeconds = MathF.Max(0f, _runtime.ParryMissedTextRemainingSeconds);
        _runtime.StatusBlockTextRemainingSeconds = MathF.Max(0f, _runtime.StatusBlockTextRemainingSeconds);

        if (!_runtime.ParryWindowActive && (_runtime.ParryWindowRemainingSeconds > 0f || _runtime.ParryWindowElapsedSeconds > 0f))
        {
            _runtime.ParryWindowRemainingSeconds = 0f;
            _runtime.ParryWindowElapsedSeconds = 0f;
        }

        if (_runtime.InputState != ParryInputState.WhiffLockout && _runtime.WhiffLockoutRemainingSeconds > 0f)
        {
            _runtime.WhiffLockoutRemainingSeconds = 0f;
            _runtime.WhiffLockoutTotalSeconds = 0f;
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

        // Retry the script patch every frame while in test20 until it succeeds.
        // The single attempt at event_setup time can miss if the worker isn't registered yet.
        if (isTitle && !_startupTest20PatchApplied)
        {
            try_patch_startup_test20_script("pre_update");
        }

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

        if (redirected || _startupForceAttemptCount <= 8 || (_startupForceAttemptCount % 10) == 0)
        {
            log_debug(
                $"Startup skip forced (event={currentEventId}, name={currentEventName}, redirected={redirected}, attempts={_startupForceAttemptCount}).");
        }

        if (redirected || isTitle)
        {
            try_patch_startup_test20_script("pre_update");
        }
    }

    private void h_startup_event_setup(uint eventId)
    {
        if (_startupEventTraceCount < 8)
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
        uint targetEventId = eventId;
        if (is_startup_splash_event(eventId, eventName))
        {
            _logger.Info($"[Parry] Startup redirect: {(string.IsNullOrWhiteSpace(eventName) ? "event" : eventName)} ({eventId}) -> test20 ({StartupSkipTitleRoomId}).");
            targetEventId = StartupSkipTitleRoomId;
        }

        _hAtelEventSetUp.orig_fptr(targetEventId);

        if (is_startup_title_event(targetEventId, eventName))
        {
            try_patch_startup_test20_script("event_setup", targetEventId, eventName);
        }
    }

    private void try_patch_startup_test20_script(string source, uint? eventIdHint = null, string eventNameHint = "")
    {
        if (_startupTest20PatchApplied || !startup_skip_mutations_enabled())
        {
            return;
        }

        int currentEventId = *FhFfx.Globals.event_id;
        string currentEventName = currentEventId > 0 ? get_current_event_name((uint)currentEventId) : string.Empty;
        bool isTitleEvent = eventIdHint.HasValue
            ? is_startup_title_event(eventIdHint.Value, eventNameHint)
            : is_startup_title_event((uint)Math.Max(0, currentEventId), currentEventName);
        if (!isTitleEvent)
        {
            return;
        }

        if (!try_resolve_loaded_test20_code(
            out Fahrenheit.Atel.AtelWorkerController* controller,
            out Fahrenheit.Atel.AtelBasicWorker* worker,
            out Fahrenheit.Atel.AtelScriptChunk* scriptChunk,
            out byte* code,
            out StartupCodeSource codeSource,
            out int controllerSlot,
            out string resolveRejectReason))
        {
            if (!_startupDiagResolveFailureLogged)
            {
                _startupDiagResolveFailureLogged = true;
                _logger.Warning(
                    $"[Parry] Startup test20 patch resolve failed ({source}): {resolveRejectReason}.");
            }
            return;
        }

        int patchedCount = 0;
        int alreadyPatched = 0;

        foreach (StartupScriptPatch patch in StartupTest20SplashPatches)
        {
            int patchReadCount = Math.Max(patch.Expected.Length, patch.Payload.Length);
            if (!is_offset_range_valid(scriptChunk->code_length, patch.Offset, patchReadCount))
            {
                _logger.Warning(
                    $"[Parry] Startup patch reject ({source}): {patch.Label} out of range at 0x{patch.Offset:X4} (len=0x{scriptChunk->code_length:X4}).");
                return;
            }

            byte* target = code + patch.Offset;
            if (!is_memory_region_accessible(target, (nuint)patchReadCount, requireWrite: false))
            {
                _logger.Warning(
                    $"[Parry] Startup patch reject ({source}): {patch.Label} target unreadable at 0x{patch.Offset:X4}.");
                return;
            }

            if (!is_memory_region_accessible(target, (nuint)patch.Payload.Length, requireWrite: true))
            {
                _logger.Warning(
                    $"[Parry] Startup patch reject ({source}): {patch.Label} target not writable at 0x{patch.Offset:X4}.");
                return;
            }

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
                        $"[Parry] Startup test20 patch aborted at {patch.Label} (offset=0x{patch.Offset:X4}): unexpected loaded script bytes.");
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
                $"[Parry] Startup test20 splash patch applied via {source} (patched={patchedCount}, already={alreadyPatched}, codeSource={codeSource}, slot={controllerSlot}).");
        }
    }

    private static bool try_resolve_loaded_test20_code(
        out Fahrenheit.Atel.AtelWorkerController* controller,
        out Fahrenheit.Atel.AtelBasicWorker* worker,
        out Fahrenheit.Atel.AtelScriptChunk* scriptChunk,
        out byte* code,
        out StartupCodeSource codeSource,
        out int controllerSlot,
        out string rejectionReason)
    {
        controller = null;
        worker = null;
        scriptChunk = null;
        code = null;
        codeSource = StartupCodeSource.None;
        controllerSlot = -1;
        StringBuilder rejectDetails = new(256);
        rejectionReason = "no_safe_candidate";

        // Fastest path during event setup: currently active worker can already point at the script
        // even when controller-level chunk references are not yet wired.
        try
        {
            Fahrenheit.Atel.AtelBasicWorker* currentWorker = FhFfx.Globals.Atel.current_worker;
            if (try_get_test20_script_from_worker(currentWorker, out scriptChunk, out code, out string workerRejectReason))
            {
                worker = currentWorker;
                controller = FhFfx.Globals.Atel.current_controller;
                controllerSlot = get_controller_slot(controller);
                codeSource = StartupCodeSource.CurrentWorker;
                return true;
            }

            append_startup_resolve_reject(rejectDetails, "current_worker", workerRejectReason);
        }
        catch
        {
            append_startup_resolve_reject(rejectDetails, "current_worker", "exception");
        }

        // Current controller worker(0) path. In startup this can be linked earlier than controller->script_chunk.
        try
        {
            Fahrenheit.Atel.AtelWorkerController* candidate = FhFfx.Globals.Atel.current_controller;
            if (try_get_test20_script_from_controller_worker0(candidate, out Fahrenheit.Atel.AtelBasicWorker* controllerWorker, out scriptChunk, out code, out string worker0RejectReason))
            {
                controller = candidate;
                worker = controllerWorker;
                controllerSlot = get_controller_slot(controller);
                codeSource = StartupCodeSource.CurrentControllerWorker0;
                return true;
            }

            append_startup_resolve_reject(rejectDetails, "current_controller.worker0", worker0RejectReason);
        }
        catch
        {
            append_startup_resolve_reject(rejectDetails, "current_controller.worker0", "exception");
        }

        // Primary controller chunk path.
        try
        {
            Fahrenheit.Atel.AtelWorkerController* candidate = FhFfx.Globals.Atel.current_controller;
            if (try_get_test20_script_from_controller(candidate, out scriptChunk, out code, out string controllerRejectReason))
            {
                controller = candidate;
                controllerSlot = get_controller_slot(controller);
                codeSource = StartupCodeSource.CurrentController;
                return true;
            }

            append_startup_resolve_reject(rejectDetails, "current_controller.chunk", controllerRejectReason);
        }
        catch
        {
            append_startup_resolve_reject(rejectDetails, "current_controller.chunk", "exception");
        }

        // Bounded array scan fallback: event setup can load test20 on a controller slot that is
        // not the current controller yet.
        try
        {
            Fahrenheit.Atel.AtelWorkerController* controllersBase = FhFfx.Globals.Atel.controllers;
            if (controllersBase != null)
            {
                for (int slot = 0; slot < StartupControllerScanLimit; slot++)
                {
                    Fahrenheit.Atel.AtelWorkerController* candidate = controllersBase + slot;
                    if (try_get_test20_script_from_controller_worker0(candidate, out Fahrenheit.Atel.AtelBasicWorker* candidateWorker, out scriptChunk, out code, out string worker0RejectReason))
                    {
                        controller = candidate;
                        worker = candidateWorker;
                        controllerSlot = slot;
                        codeSource = StartupCodeSource.ControllersArrayWorker0;
                        return true;
                    }

                    append_startup_resolve_reject(rejectDetails, $"controllers[{slot}].worker0", worker0RejectReason);

                    if (try_get_test20_script_from_controller(candidate, out scriptChunk, out code, out string arrayRejectReason))
                    {
                        controller = candidate;
                        controllerSlot = slot;
                        codeSource = StartupCodeSource.ControllersArray;
                        return true;
                    }

                    append_startup_resolve_reject(rejectDetails, $"controllers[{slot}].chunk", arrayRejectReason);
                }
            }
            else
            {
                append_startup_resolve_reject(rejectDetails, "controllers_base", "null");
            }
        }
        catch
        {
            append_startup_resolve_reject(rejectDetails, "controllers_array", "exception");
        }

        if (rejectDetails.Length > 0)
        {
            rejectionReason = rejectDetails.ToString();
        }

        return false;
    }

    private static bool try_get_test20_script_from_controller_worker0(
        Fahrenheit.Atel.AtelWorkerController* controller,
        out Fahrenheit.Atel.AtelBasicWorker* worker,
        out Fahrenheit.Atel.AtelScriptChunk* scriptChunk,
        out byte* code,
        out string rejectionReason)
    {
        worker = null;
        scriptChunk = null;
        code = null;
        rejectionReason = string.Empty;

        if (controller == null)
        {
            rejectionReason = "controller:null";
            return false;
        }

        if (!is_memory_region_accessible(controller, (nuint)sizeof(Fahrenheit.Atel.AtelWorkerController), requireWrite: false))
        {
            rejectionReason = "controller:unreadable";
            return false;
        }

        if (controller->runnable_script_count == 0)
        {
            rejectionReason = "controller:runnable_script_count_zero";
            return false;
        }

        Fahrenheit.Atel.AtelBasicWorker* candidateWorker = controller->worker(0);
        if (candidateWorker == null)
        {
            rejectionReason = "controller:worker0:null";
            return false;
        }

        if (!is_memory_region_accessible(candidateWorker, (nuint)sizeof(Fahrenheit.Atel.AtelBasicWorker), requireWrite: false))
        {
            rejectionReason = "controller:worker0:unreadable";
            return false;
        }

        if (!try_get_test20_script_from_worker(candidateWorker, out scriptChunk, out code, out string workerRejectReason))
        {
            rejectionReason = $"controller:worker0:{workerRejectReason}";
            return false;
        }

        worker = candidateWorker;
        return true;
    }

    private static bool try_get_test20_script_from_controller(
        Fahrenheit.Atel.AtelWorkerController* controller,
        out Fahrenheit.Atel.AtelScriptChunk* scriptChunk,
        out byte* code,
        out string rejectionReason)
    {
        scriptChunk = null;
        code = null;
        rejectionReason = string.Empty;

        if (controller == null)
        {
            rejectionReason = "controller:null";
            return false;
        }

        if (!is_memory_region_accessible(controller, (nuint)sizeof(Fahrenheit.Atel.AtelWorkerController), requireWrite: false))
        {
            rejectionReason = "controller:unreadable";
            return false;
        }

        return try_get_test20_script_from_chunk(controller->script_chunk, out scriptChunk, out code, out rejectionReason);
    }

    private static bool try_get_test20_script_from_worker(
        Fahrenheit.Atel.AtelBasicWorker* worker,
        out Fahrenheit.Atel.AtelScriptChunk* scriptChunk,
        out byte* code,
        out string rejectionReason)
    {
        scriptChunk = null;
        code = null;
        rejectionReason = string.Empty;

        if (worker == null)
        {
            rejectionReason = "worker:null";
            return false;
        }

        if (!is_memory_region_accessible(worker, (nuint)sizeof(Fahrenheit.Atel.AtelBasicWorker), requireWrite: false))
        {
            rejectionReason = "worker:unreadable";
            return false;
        }

        return try_get_test20_script_from_chunk(worker->script_chunk, out scriptChunk, out code, out rejectionReason);
    }

    private static bool try_get_test20_script_from_chunk(
        Fahrenheit.Atel.AtelScriptChunk* candidateChunk,
        out Fahrenheit.Atel.AtelScriptChunk* scriptChunk,
        out byte* code,
        out string rejectionReason)
    {
        scriptChunk = null;
        code = null;
        rejectionReason = string.Empty;
        if (!try_validate_test20_patch_buffer(candidateChunk, out byte* candidateCode, out _, out rejectionReason))
        {
            return false;
        }

        scriptChunk = candidateChunk;
        code = candidateCode;
        return true;
    }

    private static bool try_validate_test20_patch_buffer(
        Fahrenheit.Atel.AtelScriptChunk* scriptChunk,
        out byte* code,
        out uint codeLength,
        out string rejectionReason)
    {
        code = null;
        codeLength = 0;
        rejectionReason = string.Empty;
        if (scriptChunk == null)
        {
            rejectionReason = "chunk:null";
            return false;
        }

        if (!is_memory_region_accessible(scriptChunk, (nuint)sizeof(Fahrenheit.Atel.AtelScriptChunk), requireWrite: false))
        {
            rejectionReason = "chunk:unreadable";
            return false;
        }

        uint chunkCodeLength = scriptChunk->code_length;
        if (chunkCodeLength < StartupTest20PatchRequiredCodeLength || chunkCodeLength > StartupMaxSafeCodeLength)
        {
            rejectionReason = $"chunk:code_length_invalid:{chunkCodeLength}";
            return false;
        }

        uint chunkCodeOffset = scriptChunk->offset_code;
        if (chunkCodeOffset == 0 || chunkCodeOffset > StartupMaxSafeCodeOffset)
        {
            rejectionReason = $"chunk:code_offset_invalid:{chunkCodeOffset}";
            return false;
        }

        nuint chunkBase = unchecked((nuint)scriptChunk);
        nuint candidateCode = chunkBase + chunkCodeOffset;
        if (candidateCode < chunkBase)
        {
            rejectionReason = "chunk:code_ptr_overflow";
            return false;
        }

        byte* chunkCode = (byte*)candidateCode;
        if (!is_memory_region_accessible(chunkCode, chunkCodeLength, requireWrite: false))
        {
            rejectionReason = "chunk:code_unreadable";
            return false;
        }

        foreach (StartupScriptPatch patch in StartupTest20SplashPatches)
        {
            int byteCount = Math.Max(patch.Expected.Length, patch.Payload.Length);
            if (!is_offset_range_valid(chunkCodeLength, patch.Offset, byteCount))
            {
                rejectionReason = $"chunk:patch_out_of_range:{patch.Label}";
                return false;
            }

            byte* target = chunkCode + patch.Offset;
            if (!bytes_match(target, patch.Expected) && !bytes_match(target, patch.Payload))
            {
                rejectionReason = $"chunk:signature_mismatch:{patch.Label}";
                return false;
            }
        }

        code = chunkCode;
        codeLength = chunkCodeLength;
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

    private static void append_startup_resolve_reject(StringBuilder buffer, string source, string reason)
    {
        if (buffer == null || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        if (buffer.Length > 0)
        {
            buffer.Append(" | ");
        }

        buffer.Append(source);
        buffer.Append(':');
        buffer.Append(string.IsNullOrWhiteSpace(reason) ? "unknown" : reason);
    }

    private static int get_controller_slot(Fahrenheit.Atel.AtelWorkerController* controller)
    {
        if (controller == null)
        {
            return -1;
        }

        try
        {
            Fahrenheit.Atel.AtelWorkerController* controllersBase = FhFfx.Globals.Atel.controllers;
            if (controllersBase == null)
            {
                return -1;
            }

            nint delta = (nint)controller - (nint)controllersBase;
            int stride = sizeof(Fahrenheit.Atel.AtelWorkerController);
            if (stride <= 0 || delta < 0 || (delta % stride) != 0)
            {
                return -1;
            }

            return (int)(delta / stride);
        }
        catch
        {
            return -1;
        }
    }

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;

    private static bool is_memory_region_accessible(void* address, nuint size, bool requireWrite)
    {
        if (address == null || size == 0)
        {
            return false;
        }

        nuint start = unchecked((nuint)address);
        nuint end = start + size;
        if (end < start)
        {
            return false;
        }

        nuint cursor = start;
        while (cursor < end)
        {
            nuint queried = VirtualQuery((nint)cursor, out MemoryBasicInformation mbi, (nuint)Marshal.SizeOf<MemoryBasicInformation>());
            if (queried == 0 || mbi.RegionSize == 0)
            {
                return false;
            }

            if (mbi.State != MemCommit)
            {
                return false;
            }

            uint protect = mbi.Protect;
            if ((protect & PageGuard) != 0 || (protect & PageNoAccess) != 0)
            {
                return false;
            }

            uint baseProtect = protect & 0xFF;
            if (requireWrite)
            {
                bool writable = baseProtect == PageReadWrite
                    || baseProtect == PageWriteCopy
                    || baseProtect == PageExecuteReadWrite
                    || baseProtect == PageExecuteWriteCopy;
                if (!writable)
                {
                    return false;
                }
            }
            else
            {
                bool readable = baseProtect == PageReadOnly
                    || baseProtect == PageReadWrite
                    || baseProtect == PageWriteCopy
                    || baseProtect == PageExecuteRead
                    || baseProtect == PageExecuteReadWrite
                    || baseProtect == PageExecuteWriteCopy;
                if (!readable)
                {
                    return false;
                }
            }

            nuint regionStart = unchecked((nuint)mbi.BaseAddress);
            nuint regionEnd = regionStart + mbi.RegionSize;
            if (regionEnd <= cursor)
            {
                return false;
            }

            cursor = Math.Min(end, regionEnd);
        }

        return true;
    }

    private static bool is_offset_range_valid(uint codeLength, int offset, int byteCount)
    {
        if (offset < 0 || byteCount <= 0)
        {
            return false;
        }

        return offset <= codeLength && byteCount <= (codeLength - offset);
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
