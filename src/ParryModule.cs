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

    // MsBtlSetHitEffect — engine's registered-hit-effect emitter (global handle).
    // Used directly (no hook) on parry success to fire the Sentinel barrier
    // visual (effect 0x4A) on the parrying character. Routes through the global
    // particle handle so it is safe on PC slots.
    // Script analog: Battle.btlSetHitEffReg [70E6h].
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsBtlSetHitEffectProbe(byte chr_id, int p1, int effect_id, int p3);

    // MsScreenSetShake — the engine's native screen shake (ATEL: camSetShake family).
    // The callee truncates mode/duration/amplitude/randomness to byte/ushort itself; they are
    // declared 32-bit here because that is what the cdecl stack slots actually are.
    // See ExternalMemoryOffsetMap.Functions.MsScreenSetShake for the recovered semantics.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsScreenSetShakeProbe(
        uint screen_id, uint axis_mask, uint mode, float freq, uint duration, uint amplitude, uint randomness);

    // MsSetMotion — battler motion setter. It enqueues an ATEL motion script onto the actor's own
    // execspace (slot+5) plus a shared control execspace (3); different actors therefore animate in
    // parallel, and a second call on the same actor *restarts* its script rather than queueing.
    //
    // The 3rd parameter is NOT a chr_id. Ghidra names it that only because MsDamageSetMotion happens
    // to pass its own chr_id through; MsSetMotion merely tests it against zero (FFX.exe.c:857945):
    //   != 0  -> "hold": writes field_0xdf2 and holds field_0x432 (motion-active) until MsTerminateMotion
    //   == 0  -> clears field_0x432 again at the tail
    // The engine's Defend code passes 0 (a guard brace should not make anyone wait); the native
    // damage reaction passes non-zero. We inherited the 0 from the Defend call shape.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsSetMotionProbe(int slot, int motion_id, int hold_motion_active, byte p4, int p5, int p6, int p7);

    // MsSetChrVisible(slot, visible) — re-show a battler model hidden by a status/death effect.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsSetChrVisibleProbe(int slot, int visible);

    // MsResetBindEffect(slot) — engine's per-character effect reset (used in MsBtlChrFree).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsResetBindEffectProbe(byte slot);

    // MsLimitUp(chr_id, chr, amount) — native overdrive charge. Returns the amount actually
    // applied (after Double/Triple-Overdrive and aura multipliers). See ExternalMemoryOffsetMap.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint MsLimitUpProbe(uint chr_id, Chr* chr, uint amount);

    // MsEffectEndMotion(chr_id, mode) — engine's "battler motion finished" handler. Hooked
    // observe-only to measure played-motion durations (animation-driven-timing research).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsEffectEndMotionProbe(uint chr_id, int mode);

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
    private bool _optionOverdriveBoost = true;
    // Not a toggle: damage negation IS the mod. With it off a successful parry does nothing,
    // which is what the "enabled" master switch is for. Kept as a named constant so the guard
    // sites keep documenting where negation applies.
    private const bool _optionNegateDamage = true;
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
    // NOTE: leave OFF by default. Enabling this installs 7 additional Stage-1 native probe hooks
    // (install_stage1_probes) that are a separate research feature and crash at battle start when
    // untested. Camera/overlay debugging does NOT need this — use _optionCameraProbe + logging.
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
    // Splits MsAtelRequestMagicCamera out of the Battle Camera Lock so it can be switched off on
    // its own. Enemy spell casts route their camera through that function; suppressing it without
    // calling orig is suspected of also swallowing the spell VFX. Default true keeps the previous
    // behaviour, so this is a measurement switch, not a fix.
    private bool _optionMagicCameraLock = true;
    // Visual feedback effect on a successful parry: fires the Sentinel barrier
    // visual (effect 0x4A — golden ring / shield-of-air spatial particle) on
    // the parrying character via the global-handle emitter MsBtlSetHitEffect
    // (0x0039EC60). Effect is PC-safe: the engine fires this exact call on party
    // actors when an attack lands on a Sentinel-statused PC (forensic ref:
    // FUN_0079E530). Default-on.
    private bool _optionParryEffect = true;

    // Deterministic motion termination for the dodge. The dodge's ATEL motion script is restarted by
    // every press and, left alone, only ends when it runs out — which is why spamming the button used
    // to stall the enemy's charging cast. Instead of swallowing presses, we guarantee that every dodge
    // *terminates*: on a new press (clean restart), on the resolving hit, and on window expiry.
    //
    // MsEffectEndMotion is the engine's own end-of-motion entry. It is a NO-OP unless the ATEL worker
    // has actually started the motion, which it signals by setting Chr+0x3f3 (motion-disable) — so we
    // gate on that byte rather than guessing.
    private const int ChrMotionDisableOffset = 0x3f3;

    // MsEffectEndMotion's `mode`: 3 issues the return-to-idle motion but SKIPS MsSetChrMoveFlag(chr,0)
    // (FFX.exe.c:827958). That matters because motion != move: the step-out and walk-back are driven by
    // the move machine (Chr+0x415), not by the animation. Mode 3 should end the animation while leaving
    // the walk-back running; mode 0 would clear the move flag and risk stranding the actor. Untested —
    // this const exists so both can be compared in-game.
    private const int DodgeEndMotionMode = 3;
    private bool _optionDodgeMotionCancel = true;

    // Impact screen shake: fire the engine's own decaying screen shake when a hit is *met* —
    // on a successful parry, and only there. Every dodge avoids the hit, PERFECT included; PERFECT
    // merely reports that the dodge landed inside the parry window. Do NOT read
    // CombatLabelPalette.preciseTiming (which tints PARRIED and PERFECT alike) as "both met the
    // hit" — it groups them by timing readability, not by impact. Default-on.
    private bool _optionImpactShake = true;

    // Shake parameters for MsScreenSetShake. The engine evaluates, per axis:
    //   offset = sin(phase) * amplitude * (32 + jitter)/32 * (remaining/total)
    // with `phase += freq` once per frame and per-axis phase/freq slots (+0x13C/+0x144 and
    // +0x140/+0x148). Both phases start at 0, so firing ONE call with axis_mask = 3 gives both axes
    // the same phase, frequency and amplitude — the offset then traces a straight 45° line, not a
    // shake. That is the bug the first version shipped; it read as "very vertical, both-sided".
    //
    // We therefore fire the two axes SEPARATELY with decorrelated frequencies. The shape of the
    // values follows standard game-feel practice for an impact shake (Squirrel Eiserloh, GDC 2016,
    // "Juicing Your Cameras With Math"): short, high-frequency, low-amplitude, decaying, with
    // independent axes. Cinemachine's default impulse is likewise ~0.2 s.
    //
    // Converting to this engine: Hz = freq * 30 / (2*PI) at the 30 fps battle tick, and Nyquist caps
    // us at 15 Hz — so the usual 10-20 Hz impact band has to sit at its lower edge.
    private const uint ImpactShakeScreenId   = 0;      // screen_id must be < 3
    private const uint ImpactShakeAxisA      = 1;      // axis_mask bit 0
    private const uint ImpactShakeAxisB      = 2;      // axis_mask bit 1
    private const uint ImpactShakeModeDecay  = 1;      // envelope = remaining/total → fades out

    private const float ImpactShakeFreqA     = 1.7f;   // ~8.1 Hz
    private const float ImpactShakeFreqB     = 2.3f;   // ~11.0 Hz — ratio 1.35, so the axes never relock
    private const uint ImpactShakeAmpA       = 9;      // a parry is a lateral impact: favour one axis
    private const uint ImpactShakeAmpB       = 5;
    private const uint ImpactShakeDuration   = 8;      // ticks at 30 fps ≈ 0.27 s — used when the sweep is off
    private const uint ImpactShakeRandomness = 8;      // the engine's own jitter: ±4 around the amplitude

    // Duration sweep, for dialling the shake in by feel. ONLY the duration varies — amplitude and the
    // two frequencies stay fixed, so each shake differs in exactly one dimension and the comparison
    // stays clean. Stages are drawn from a shuffled bag rather than in order: a rising or falling
    // sequence would let you compare each shake against its neighbour instead of judging it on its
    // own, and the same stage never lands twice in a row.
    private static readonly uint[] ImpactShakeDurationPresets = [5, 8, 12, 16, 22];   // ticks @30fps
    private static readonly string[] ImpactShakeDurationLabels = ["A", "B", "C", "D", "E"];
    private const int ImpactShakeDefaultPreset = 1;    // "B" = 8 ticks, the value the sweep replaces

    private bool _optionImpactShakeSweep = true;
    private int[] _shakeBag = [];
    private int _shakeBagPos;
    private int _lastShakePreset = -1;
    private readonly int[] _shakePresetCounts = new int[5];

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
    // Native PC evasion is unconditionally disabled by h_ms_dmg_calc_check_hit — the manual dodge
    // system replaced it, and a PC that evades natively never reaches our impact path. There is
    // deliberately no toggle: turning it back on is a bug state, not a game mode.
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
    // ── Dodge / native-evade (Circle ○) ─────────────────────────────────────
    // Reactive dodge on Circle: on press, the step-out MOVEMENT starts immediately for each
    // targeted PC — we call the engine's own case-1 evade (MsDamageSetMotion param_2=1 → avoid
    // move-mode ram.field_0x425 via FUN_0078f090 → positional step-back + motion 0xC). A cue-based
    // window is armed at the same time; if a hit from that attacker lands while the window is
    // valid, the MsDamageSetMotion hook NEGATES the damage + suppresses the flinch (the movement
    // already plays, so no second trigger). Attacker-keyed → AoE. Never feeds the streak/counter
    // path (no counterattack). The engine drives the return/walk-back.
    private bool _optionDodgeEnabled = true;
    private bool _dodgeWindowActive = false;
    private float _dodgeWindowRemainingSeconds = 0f;
    private byte _dodgeArmedAttackerId = 0;
    // CueFirstSeenFrame of the attack the dodge was armed for — the negation only applies to THIS
    // attack instance, so a multi-hit of the same attack is fully dodged but a fresh attack from
    // the same attacker landing in the still-open window is NOT auto-dodged without a new press.
    private ulong _dodgeArmedCueFrame = 0;
    // Party slots the dodge was armed for, snapshotted at press time from the cue's filtered,
    // parryable target mask (the same set that receives the step-out). A slot may resolve as
    // evaded at impact only if its bit is set here — the engine drives the p5=0/motion commit for
    // EVERY party slot, so the cue-wide window/attacker checks alone cannot keep an untargeted slot
    // from resolving. Mirrors the parry's per-slot _parryExpiry discipline. Refreshed on each fresh
    // press; cleared with _dodgeResolvedAtImpactMask at cue end and on runtime reset.
    private uint _dodgeArmedTargetMask = 0;
    // Slots that have resolved as evaded for the current cue. The dodge equivalent of the parry's
    // LastParriedTargetMask: durable, survives the wall-clock window and any cue mutation, and is
    // cleared only at cue end. Gates BOTH MsSetDamageInternal commit passes (p5=0 and p5=1024), so
    // a delayed-finalization attack cannot land HP, death or status after the window closed. Kept
    // separate from _parryResolvedAtImpactMask so no "PARRIED" text is drawn over the "DODGE" text.
    private uint _dodgeResolvedAtImpactMask = 0;
    // Slots whose dodge landed inside the (tighter) parry window — "PERFECT" instead of "DODGE",
    // plus the same overdrive boost a parry grants. Decided at impact, because the durable dodge
    // marker lets the damage commit arrive long after the wall-clock window has closed. Lives with
    // the label (cleared when the DODGE/PERFECT text expires), not with the cue.
    private uint _dodgeTextPerfectMask = 0;
    private long _dodgeEvadeCount = 0;
    // Step-out driven minimally: the avoid move-mode byte (Chr+0x425, what FUN_0078f090 sets)
    // + the evade animation (motion 0xC). Deliberately NOT via MsDamageSetMotion, whose case 1
    // also sets the hit-terminate flag field_0x433 — read by battle-update logic and, poked
    // out-of-band during a multi-phase cast chargeup, it desyncs the enemy action → soft-lock.
    private const int ChrEvadeMoveModeOffset = 0x425;
    // Move PHASE byte (Chr+0x415, from the evade-probe log): 0x09 = stepping out, 0x01 = walking
    // back, 0x00 = idle/home. Used to pace re-presses to the actual move duration.
    private const int ChrEvadeMovePhaseOffset = 0x415;
    // Last-attacker id (Chr+0xdef). MsDamageSetMotion's case 1 refreshes it from chr->attacker_id_
    // right before playing motion 0xC; the motion's battle-ATEL script reads it to orient the
    // step-out away from that attacker. Never read from C code — only written there.
    private const int ChrLastAttackerIdOffset = 0xdef;
    private const int EvadeMotionId = 0xC;
    private const float DodgeWindowMsNormal = 350f;   // release default (parry Normal = 200ms)
    private const float DodgeWindowMsDebug  = 800f;    // DEBUG default — generous for testing
    private const float DodgeWindowMsMin = 100f;
    private const float DodgeWindowMsMax = 1200f;
    // Adjustable at runtime via the "dodge_window" setting (slider, persisted). Defaults to the
    // DEBUG window in DEBUG builds, the normal window otherwise.
    private float _dodgeWindowMs =
#if DEBUG
        DodgeWindowMsDebug;
#else
        DodgeWindowMsNormal;
#endif
    private float DodgeWindowSeconds => _dodgeWindowMs / 1000f;
    // Dodge whiffout: a short recovery after a step-out before the next one is allowed — paces
    // multi-press. Adjustable via the "dodge_whiffout" setting (0 = no cooldown).
    private const float DodgeWhiffoutMsMin = 0f;
    private const float DodgeWhiffoutMsMax = 2000f;
    private float _dodgeWhiffoutMs = 0f;
    private float _dodgeWhiffoutRemainingSeconds = 0f;
    // "DODGE" success text overlay (mirrors the parry "PARRIED" overlay), per targeted slot.
    private float _dodgeTextRemainingSeconds = 0f;
    private uint _dodgeTextTargetMask = 0;
    // Per-appearance random seed so each DODGE/PARRIED label's entry (skew/rotation/scale) varies a
    // little, so no two labels land identically. (An earlier comment here claimed FFX has no
    // camera shake; it does — see ExternalMemoryOffsetMap.Functions.MsScreenSetShake.)
    private static readonly Random _labelRng = new();
    private float _dodgeTextSeed = 0f;
    private float _parriedTextSeed = 0f;
    private float next_label_seed() => (float)_labelRng.NextDouble() * 1000f;
    // Debug probe: logs Chr+0x415/0x425/0x4AC (move-mode/avoid/motion-type) + world position for
    // the stepped-out slot(s) for a short window after a step-out — to find the move distance/mode.
    private uint _dodgeProbeSlotsMask = 0;
    private int _dodgeProbeFramesLeft = 0;
    // Native parry block (A): flag the parrying char as guarding (ChrRam+0x19A) so the engine's
    // MsDamageSetMotion plays the block reaction 0x43 itself at the real impact — the same field
    // it sets for Sentinel/Defend. When off, falls back to the manual MsSetMotion(0x43) poke.
    private bool _optionParryNativeBlock = true;
    // Camera probe (debug): logs EVERY camera hook invocation + the lock-gating state (not just
    // when suppressed) so an un-locked enemy camera pan reveals which path fired and why the lock
    // did not engage (turn/attacker gating). Toggle via the "camera_probe" setting.
    private bool _optionCameraProbe =
#if DEBUG
        true;
#else
        false;
#endif
    // Guard/defend reaction flag, relative to the ChrRam sub-struct (Chr.ram). Set to 1 →
    // MsDamageSetMotion overrides flinch reactions 9/0x30 to the block motion 0x43.
    private const int ChrRamGuardReactFlagOffset = 0x19A;
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
    // One-shot guard for the read-only overdrive-mask save probe: fires exactly
    // once, the first time a live battle context exists in the process.
    private bool _saveDataOverdriveProbeFired;
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
    private readonly FhMethodHandle<MsDmgCalcCheckHitProbe> _hMsDmgCalcCheckHit;
    private readonly FhMethodHandle<MsEffectEndMotionProbe> _hMsEffectEndMotion;
    // Frame a motion was last played per party slot (lab Play / parry block), 0 = none.
    // Read by the observe-only MsEffectEndMotion hook to log the motion's run length.
    private readonly ulong[] _motionPlayFrame = new ulong[PartyActorCapacity];

    // Frame the parry block reaction (0x43) was last played on a slot, by EITHER the native path
    // (guard flag + orig MsDamageSetMotion) or the manual MsSetMotion poke. Whichever runs first
    // stamps it; the other then stands down. That keeps exactly one driver per hit — the
    // double-drive was the old "parry twitch" — while guaranteeing the block always plays, even
    // for parries resolved at MsSetDamageInternal (which never reach MsDamageSetMotion at all).
    private readonly ulong[] _parryBlockPlayedFrame = new ulong[PartyActorCapacity];
    private const ulong ParryBlockRecentFrames = 3;

    private bool parry_block_recently_played(int slot)
        => (uint)slot < PartyActorCapacity
           && _parryBlockPlayedFrame[slot] != 0
           && _debugFrameIndex - _parryBlockPlayedFrame[slot] <= ParryBlockRecentFrames;

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
        _hMsDmgCalcCheckHit = new FhMethodHandle<MsDmgCalcCheckHitProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsDmgCalcCheckHit, h_ms_dmg_calc_check_hit); // MsDmgCalc_CheckHit — accuracy/evasion roll; intercepted to disable native evasion for real PCs
        _hMsEffectEndMotion = new FhMethodHandle<MsEffectEndMotionProbe>(this, "FFX.exe", ExternalMemoryOffsetMap.Functions.MsEffectEndMotion, h_ms_effect_end_motion); // observe-only: measure played-motion durations

        _hStartupAtelEventSetUp    = new FhMethodHandle<StartupAtelEventSetUp>(this, "FFX.exe", StartupOffsets.AtelEventSetUp, h_startup_event_setup);
        _hStartupNeedShowJapanLogo = new FhMethodHandle<StartupNeedShowJapanLogo>(this, "FFX.exe", StartupOffsets.NeedShowJapanLogo, h_startup_need_show_japan_logo);
        _hStartupBootFmvSkip       = new FhMethodHandle<StartupFmvSkipPoll>(this, "FFX.exe", StartupOffsets.FmvSkipPoll, h_startup_boot_fmv_skip);
        _hStartupShellExecuteW     = new FhMethodHandle<StartupShellExecuteW>(this, "shell32.dll", "ShellExecuteW", h_startup_shell_execute_w);

        settings = new FhSettingsCategory("fhparry", [
            new FhSettingCustomRenderer("enabled", render_setting_enabled),
            new FhSettingCustomRenderer("difficulty", render_setting_difficulty),
            new FhSettingCustomRenderer("audio", render_setting_audio),
            new FhSettingCustomRenderer("ctb", render_setting_overdrive_boost),
            new FhSettingCustomRenderer("penalty", render_setting_penalty),
            new FhSettingCustomRenderer("battle_camera_lock_mode", render_setting_battle_camera_lock_mode),
            new FhSettingCustomRenderer("magic_camera_lock", render_setting_magic_camera_lock),
            new FhSettingCustomRenderer("parry_effect", render_setting_parry_effect),
            new FhSettingCustomRenderer("impact_shake", render_setting_impact_shake),
            new FhSettingCustomRenderer("dodge_motion_cancel", render_setting_dodge_motion_cancel),
            new FhSettingCustomRenderer("streak_counter", render_setting_streak_counter),
            new FhSettingCustomRenderer("dodge_window", render_setting_dodge_window),
            new FhSettingCustomRenderer("dodge_whiffout", render_setting_dodge_whiffout),
#if DEBUG
            // Diagnostics — not shipped. Release builds have no UI for these.
            new FhSettingCustomRenderer("logging", render_setting_logging),
            new FhSettingCustomRenderer("debug_overlay", render_setting_debug_overlay),
            new FhSettingCustomRenderer("camera_probe", render_setting_camera_probe),
#endif
            new FhSettingCustomRenderer("future", render_setting_future)
        ]);
    }

    public override bool init(FhModContext mod_context, FileStream global_state_file)
    {
        _settingsFilePath = resolve_settings_path(mod_context, global_state_file);
        _logger.Info($"[Parry] Settings file resolved to '{_settingsFilePath}'.");
        load_persistent_settings();
        initialize_session_logging(mod_context);
        initialize_motion_blocklist();
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
            _hMsEffectEndMotion.hook();
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Could not hook MsEffectEndMotion (motion-duration observe unavailable): {ex.Message}");
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
        tick_evade_probe();

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

        if (_optionDodgeEnabled && FhApi.Input.cancel.just_pressed)   // cancel = Circle (○)
        {
            handle_dodge_input_press(parryInput);
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

        // Dodge window tick (independent of the parry state machine). Expires by time; a
        // successful dodge is not "consumed" so a multi-hit / AoE swing is fully evaded.
        if (_dodgeWindowActive)
        {
            _dodgeWindowRemainingSeconds -= deltaSeconds;
            if (_dodgeWindowRemainingSeconds <= 0f)
            {
                _dodgeWindowActive = false;

                // Nothing hit us. End the evade animation deterministically rather than letting it run
                // out — together with the press-restart and hit paths this guarantees the dodge motion
                // always terminates, which is what stops a spammed dodge from leaving an actor
                // permanently "animating" and stalling an enemy's charging cast.
                uint stepped = _dodgeProbeSlotsMask;
                while (stepped != 0)
                {
                    int slot = BitOperations.TrailingZeroCount(stepped);
                    stepped &= stepped - 1;
                    try_end_battle_motion(slot, "window_expired");
                }

                if (_optionLogging)
                {
                    log_debug("[Dodge] Window expired without a hit.");
                }
            }
        }

        if (_dodgeWhiffoutRemainingSeconds > 0f)
        {
            _dodgeWhiffoutRemainingSeconds = MathF.Max(0f, _dodgeWhiffoutRemainingSeconds - deltaSeconds);
        }

        if (_dodgeTextRemainingSeconds > 0f)
        {
            _dodgeTextRemainingSeconds = MathF.Max(0f, _dodgeTextRemainingSeconds - deltaSeconds);
            if (_dodgeTextRemainingSeconds <= 0f)
            {
                _dodgeTextTargetMask = 0;
                _dodgeTextPerfectMask = 0;
            }
        }

        tick_dodge_field_probe();

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
        render_parry_window_overlay();
        render_dodge_overlay();
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
        _dodgeWindowActive = false;
        _dodgeWindowRemainingSeconds = 0f;
        _dodgeArmedAttackerId = 0;
        _dodgeArmedCueFrame = 0;
        _dodgeArmedTargetMask = 0;
        _dodgeResolvedAtImpactMask = 0;
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
