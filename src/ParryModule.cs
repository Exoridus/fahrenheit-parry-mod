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
    // Number of consecutive parries on a single slot at which the observe-only
    // streak path emits a "STREAK READY" log entry. Tuning point for the future
    // counter-attack feature; kept const for now so the threshold is one obvious
    // edit away when we wire MsInsertBtlCommand.
    private const byte ParryStreakObserveThreshold = 2;
    private const int DebugLogRingCapacity = 500;
    private const int CueHistoryRingCapacity = 64;
    private const int DebugTurnRowCapacity = 500;
    private delegate char* AtelGetEventName(uint eventId);
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

    // MsAtelRequestCamera — central gate for camera change requests during gameplay.
    // 8 params; return value unused at every observed call site. We intercept this
    // when an enemy turn is in progress (and the lock is enabled) to keep the camera
    // pinned to the player view so incoming attacks remain readable for parry timing.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsAtelRequestCameraProbe(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8);

    // MsAtelRequestMagicCamera — sibling of MsAtelRequestCamera for magic-spell
    // camera changes. 9 params, returns byte camera-id (0xFF = "no camera"
    // sentinel). Hooked alongside MsAtelRequestCamera so the enemy-turn camera
    // lock covers both normal-attack and magic-spell camera paths.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte MsAtelRequestMagicCameraProbe(int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9);

    // MsBattleSpecialCameraPause — engine entry point for cinematic camera mode
    // (boss / overdrive attacks). Hooked alongside MsAtelRequestCamera /
    // MsAtelRequestMagicCamera so the Battle Camera Lock covers all three camera
    // paths. Signature: void (byte mode). __cdecl. Suppression branch is a bare
    // `return` — no call to the original, no return value to fake.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsBattleSpecialCameraPauseProbe(byte mode);

    // MsBattleCameraTick — FUN_007be090, the per-frame battle-camera APPLY driver
    // (nullary void, called once/frame from Sg_MainLoop). Hooked + skip-orig'd while
    // the Battle Camera Lock is engaged so scripted-special cameras (Cactuar needles
    // etc.) — which write the camera target directly into ms_camera, bypassing the
    // three Request hooks — cannot pan. 0-arg void → calling-convention-safe.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsBattleCameraTickProbe();

    // MsBtlSetHitEffect — engine's registered-hit-effect emitter (global handle).
    // Used directly (no hook) on parry success to fire the Sentinel barrier
    // visual (effect 0x4A) on the parrying character. Routes through the global
    // particle handle so it is safe on PC slots.
    // Script analog: Battle.btlSetHitEffReg [70E6h].
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsBtlSetHitEffectProbe(byte chr_id, int p1, int effect_id, int p3);

    // MsSetMotion — battler motion setter. Safe call shape (engine's own Defend code):
    // MsSetMotion(slot, motion_id, 0, 0, 1, 0, 0). Used by the FX/Motion lab to preview poses.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsSetMotionProbe(int slot, int motion_id, int chr_id, byte p4, int p5, int p6, int p7);

    // MsInsertBtlCommand — engine call to queue a battle command for a chr to
    // execute as the next available action. Used directly (no hook) by the
    // streak counter-attack feature to inject a basic Attack from the parrier
    // onto the originally-attacking enemy at cue-clear time.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsInsertBtlCommandProbe(AttackCue* cue, int param_2, int param_3, int chr_id);

    // MsDmgCalc_CheckHit — engine accuracy/evasion roll. Hooked to override
    // MISS → HIT for real PCs (not aeons) so native evasion is disabled while
    // the manual dodge system replaces it. RNG state is preserved by always
    // invoking the original first; only the return value is overridden.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsDmgCalcCheckHitProbe(Chr* user, Chr* target, void* command, void* info, int counter);

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
    // Controls which turns trigger battle-camera suppression. EnemyTurnsOnly (default)
    // preserves the prior bool-true behaviour. AllTurns extends suppression to all
    // AwaitingTurnEnd windows. Off passes every call through to the engine unchanged.
    // MsBattleSpecialCameraPause (cinematic path) is never touched by any mode.
    private enum BattleCameraLockMode
    {
        Off = 0,
        EnemyTurnsOnly = 1,
        AllTurns = 2,
    }
    private BattleCameraLockMode _optionBattleCameraLockMode = BattleCameraLockMode.EnemyTurnsOnly;
    // Visual feedback effect on a successful parry: fires the Sentinel barrier
    // visual (effect 0x4A — golden ring / shield-of-air spatial particle) on
    // the parrying character via the global-handle emitter MsBtlSetHitEffect
    // (0x0039EC60). Effect is PC-safe: the engine fires this exact call on party
    // actors when an attack lands on a Sentinel-statused PC (forensic ref:
    // FUN_0079E530). Default-on.
    private bool _optionParryEffect = true;
    // Streak counter attack: when a slot completes a defensive streak (every
    // targeted slot in a cue parried at least once and cumulative streak ≥
    // ParryStreakObserveThreshold), queues a basic Attack from the parrier
    // onto the original attacker via MsInsertBtlCommand. Default-off pending
    // in-game verification of the MsInsertBtlCommand call address and AttackCue
    // layout. Off → log-only behaviour (the observation still runs and the
    // debug overlay still surfaces "streak ready" events).
    private bool _optionStreakCounter = false;
    // Disable native FFX evasion for real player characters (chr->ram.is_aeon == false
    // and chr->chr_id < 0x14). Aeons and monsters keep vanilla evasion. The hook always
    // invokes the original MsDmgCalc_CheckHit (so the engine's RNG advance is preserved),
    // then overrides MISS → HIT for PC targets when this option is enabled and we've
    // cached the HIT enum integer value (auto-discovered via observation; see
    // _checkHitObserved*).
    //
    // This is the prep step for the upcoming manual-dodge system that will replace
    // native evasion. Default-off until the dodge mechanic ships and the HIT/MISS
    // enum integers have been observed in-game.
    private bool _optionDisableNativeEvasion = false;
    // Counter for suppressed camera requests, reset on mode change — surfaced
    // in debug logging only when both _optionLogging and _optionBattleCameraLockMode
    // is not Off, to avoid log spam in release builds.
    private long _enemyCameraLockSuppressCount;
    private int _enemyMagicCameraLockSuppressCount = 0;
    private int _battleSpecialCameraLockSuppressCount = 0;
    // CheckHitResult enum auto-discovery. The enum has 3 members (HIT/MISS/MISS_ALIVE)
    // but the integer values weren't exported by Ghidra's datatype dumper. We learn
    // them by observation:
    //   - First N>=5 PC-target invocations returning the same value → assume HIT (most
    //     attacks land in normal play).
    //   - Any subsequent PC-target return that differs from cached HIT → MISS.
    //     (MISS_ALIVE is rare — only fires for status-only commands against non-zombie
    //     targets, command->flags_misc & 0x800000.)
    //   - Once both HIT and MISS are known, the override fires.
    // Settings file may also pre-seed these via persisted values (set by the user
    // after observing logs). Default null = unknown.
    private int? _checkHitHitValue = null;
    private int? _checkHitMissValue = null;
    private int _checkHitConsecutiveSameCount = 0;
    private int? _checkHitFirstObservedValue = null;
    private long _checkHitOverrideCount = 0;
    private long _checkHitObservationCount = 0;
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
    // Observe-only consecutive-parry streak per slot. Increments on each successful
    // parry resolution; resets when that slot whiffs, gets hit outside the window,
    // or the runtime fully resets (battle end / mod toggled off). When the streak
    // crosses ParryStreakObserveThreshold, a single "STREAK READY" log entry is
    // emitted — no behaviour change, no counter inserted yet. Wired so the engagement
    // model can be measured before any counter-attack is queued. See open follow-up:
    // promote into actual MsInsertBtlCommand counter once the mechanic feels right.
    private readonly byte[] _consecutiveParriesPerSlot = new byte[PartyActorCapacity];
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
    private bool _debugAutoScroll = true;
    private bool _debugCueAutoScroll = true;
    private float _debugCuePanelRatio = 0.50f;
    private int _debugCueTurnId;
    private string _dataMappingStatus = "No data mappings loaded.";
    private readonly Random _rng = new();
    private string _settingsFilePath = string.Empty;
    private StreamWriter? _sessionDebugLogWriter;
    private StreamWriter? _sessionTimelineLogWriter;
    private string _sessionLogsRoot = string.Empty;
    private string _sessionLogDirectory = string.Empty;
    private string _sessionLogPrefix = string.Empty;
    private bool _sessionLogDisabled;
    private bool _sessionRetentionPruned;
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
    private readonly FhMethodHandle<MsAtelRequestCameraProbe> _hMsAtelRequestCamera;
    private readonly FhMethodHandle<MsAtelRequestMagicCameraProbe> _hMsAtelRequestMagicCamera;
    private readonly FhMethodHandle<MsBattleSpecialCameraPauseProbe> _hMsBattleSpecialCameraPause;
    private readonly FhMethodHandle<MsBattleCameraTickProbe> _hMsBattleCameraTick;
    private readonly FhMethodHandle<MsDmgCalcCheckHitProbe> _hMsDmgCalcCheckHit;
    private long _battleCameraTickSuppressCount;

    public ParryModule()
    {
        _hMsExeInputCue = new FhMethodHandle<FhFfx.FhCall.MsExeInputCue>(this, "FFX.exe", FhFfx.FhCall.__addr_MsExeInputCue, h_ms_exe_input_cue);
        _hMsSetDamage = new FhMethodHandle<MsSetDamageProbe>(this, "FFX.exe", FhFfx.FhCall.__addr_MsSetDamage, h_ms_set_damage);
        _hMsDamageSetMotion = new FhMethodHandle<MsDamageSetMotionProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsDamageSetMotion, h_ms_damage_set_motion);
        _hMsCalcDamage = new FhMethodHandle<MsCalcDamageProbe>(this, "FFX.exe", FhFfx.FhCall.__addr_MsCalcDamage, h_ms_calc_damage);
        _hDmgCalcArmored = new FhMethodHandle<DmgCalcArmoredProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.DmgCalcArmored, h_dmg_calc_armored);
        _hMsCalcDamageInternal = new FhMethodHandle<MsCalcDamageInternalProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsCalcDamageInternal, h_ms_calc_damage_internal);
        _hMsSetDamageInternal = new FhMethodHandle<MsSetDamageInternalProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.DiscordCandidates.FnMsSetDamageInternal, h_ms_set_damage_internal);
        _hMsAtelRequestCamera = new FhMethodHandle<MsAtelRequestCameraProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsAtelRequestCamera, h_ms_atel_request_camera); // MsAtelRequestCamera — gates camera changes; intercepted to lock camera during enemy turns
        _hMsAtelRequestMagicCamera = new FhMethodHandle<MsAtelRequestMagicCameraProbe>(
            this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsAtelRequestMagicCamera, h_ms_atel_request_magic_camera);
        _hMsBattleSpecialCameraPause = new FhMethodHandle<MsBattleSpecialCameraPauseProbe>(
            this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsBattleSpecialCameraPause, h_ms_battle_special_camera_pause);
        _hMsBattleCameraTick = new FhMethodHandle<MsBattleCameraTickProbe>(
            this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsBattleCameraTick, h_ms_battle_camera_tick); // per-frame camera apply; skip-orig'd under the lock so scripted-special cameras can't pan
        _hMsDmgCalcCheckHit = new FhMethodHandle<MsDmgCalcCheckHitProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsDmgCalcCheckHit, h_ms_dmg_calc_check_hit); // MsDmgCalc_CheckHit — accuracy/evasion roll; intercepted to disable native evasion for real PCs

        _hStartupAtelEventSetUp    = new FhMethodHandle<StartupAtelEventSetUp>(this, "FFX.exe", StartupOffsets.AtelEventSetUp, h_startup_event_setup);
        _hStartupNeedShowJapanLogo = new FhMethodHandle<StartupNeedShowJapanLogo>(this, "FFX.exe", StartupOffsets.NeedShowJapanLogo, h_startup_need_show_japan_logo);
        _hStartupBootFmvSkip       = new FhMethodHandle<StartupFmvSkipPoll>(this, "FFX.exe", StartupOffsets.FmvSkipPoll, h_startup_boot_fmv_skip);
        _hStartupShellExecuteW     = new FhMethodHandle<StartupShellExecuteW>(this, "shell32.dll", "ShellExecuteW", h_startup_shell_execute_w);

        settings = new FhSettingsCategory("fhparry", [
            new FhSettingCustomRenderer("enabled", render_setting_enabled),
            new FhSettingCustomRenderer("difficulty", render_setting_difficulty),
            new FhSettingCustomRenderer("audio", render_setting_audio),
            new FhSettingCustomRenderer("parry_state_hud", render_setting_parry_state_hud),
            new FhSettingCustomRenderer("ctb", render_setting_overdrive_boost),
            new FhSettingCustomRenderer("logging", render_setting_logging),
            new FhSettingCustomRenderer("debug_overlay", render_setting_debug_overlay),
            new FhSettingCustomRenderer("negate", render_setting_negate),
            new FhSettingCustomRenderer("penalty", render_setting_penalty),
            new FhSettingCustomRenderer("battle_camera_lock_mode", render_setting_battle_camera_lock_mode),
            new FhSettingCustomRenderer("parry_effect", render_setting_parry_effect),
            new FhSettingCustomRenderer("streak_counter", render_setting_streak_counter),
            new FhSettingCustomRenderer("disable_native_evasion", render_setting_disable_native_evasion),
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
            _hMsAtelRequestCamera.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsAtelRequestCamera (enemy-turn camera lock unavailable): {ex.Message}");
        }

        try
        {
            _hMsAtelRequestMagicCamera.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsAtelRequestMagicCamera (enemy-turn magic camera lock unavailable): {ex.Message}");
        }

        try
        {
            _hMsBattleSpecialCameraPause.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsBattleSpecialCameraPause (boss cinematic camera lock unavailable): {ex.Message}");
        }

        try
        {
            _hMsBattleCameraTick.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsBattleCameraTick (scripted-special camera lock coverage unavailable): {ex.Message}");
        }

        try
        {
            _hMsDmgCalcCheckHit.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsDmgCalcCheckHit (disable-native-evasion unavailable): {ex.Message}");
        }

        install_stage1_probes();
        install_startup_skip_hooks();

        _logger.Info("ParryPrototype ready. Adjust options via Mod Config (F7).");
        return true;
    }

    private void on_pre_update(Fahrenheit.Events.UpdateLoopEventArgs e)
    {
        _debugFrameIndex++;
        float deltaSeconds = e.delta;
        _simulationClockSeconds += deltaSeconds;

        // Bundled startup-skip convenience runs regardless of the parry-enabled gate below.
        tick_startup_skip();

        update_debug_save_loaded_state();
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
        // Streak counter is per-slot and persists across turns; only the full
        // runtime reset (battle end / mod toggled off) clears it.
        Array.Clear(_consecutiveParriesPerSlot);

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
