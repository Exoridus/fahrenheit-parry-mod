global using Fahrenheit.FFX.Battle;
global using Fahrenheit.FFX;
global using System.Numerics;
using System.Runtime.InteropServices;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule : IDisposable
{
    private const int PartyActorCapacity = 10;
    private const float ParriedTextSeconds = 1;
    private const int ChrRamGuardReactFlagOffset = 0x19A;
    private readonly TestBattleAdapter _battleAdapter = new();
    private readonly CommitRuntime _runtime = new();
    private bool _optionEnabled = true, _optionNegateDamage = true, _optionDodgeEnabled = false;
    private bool _optionLogging = false, _dodgeWindowActive = false;
    private ulong _debugFrameIndex = 0, _dodgeArmedCueFrame = 0;
    private uint _parryResolvedAtImpactMask, _internalInterceptedMask, _latePreOpenP5ZeroCommitMask;
    private uint _dodgeResolvedAtImpactMask, _dodgeArmedTargetMask = 0, _dodgeTextTargetMask;
    private byte _dodgeArmedAttackerId = 0;
    private readonly byte[] _internalInterceptedAttackerId = new byte[PartyActorCapacity];
    private readonly byte[] _latePreOpenP5ZeroCommitAttackerId = new byte[PartyActorCapacity];
    private readonly byte[] _parryArmedAttackerId = Enumerable.Repeat((byte)22, PartyActorCapacity).ToArray();
    private readonly uint[] _preHitHpSnapshot = new uint[PartyActorCapacity];
    private readonly bool[] _parryFeedbackPending = new bool[PartyActorCapacity];
    private readonly bool[] _damageEventActive = new bool[PartyActorCapacity];
    private readonly ulong[] _parryBlockPlayedFrame = new ulong[PartyActorCapacity];
    private readonly bool _optionParryNativeBlock = false;
    private readonly CalculationTelemetry[] _attackTelemetry = Enumerable.Range(0, PartyActorCapacity).Select(_ => new CalculationTelemetry()).ToArray();
    private int _dodgeEvadeCount, _dodgeTextSeed;
    private float _dodgeTextRemainingSeconds;
    public float DodgeTextRemaining => _dodgeTextRemainingSeconds;
    public int OriginalCalls { get; private set; }
    public int Successes { get; private set; }
    public int NativeMotionCalls { get; private set; }
    public int NativeDispatchCalls { get; private set; }
    public bool WindowOpen;
    public bool Enabled { set => _optionEnabled = value; }
    public bool ChainInnerCalculation;
    public bool NativeCommitEmitsMotion;
    public Action? DuringNativeCommit;
    public bool FeedbackPending => _parryFeedbackPending[0];
    public uint ResolvedMask => _runtime.LastParriedTargetMask;
    public byte CurrentAttacker { set => _runtime.CurrentAttackerId = value; }
    public uint TargetMask { set => _runtime.CurrentPartyTargetMask = value; }
    public void MarkImpactOnly() => _parryResolvedAtImpactMask |= 1;
    public Chr* Target => _battleAdapter.GetPlayerCharacters();
    public Chr* Actor(int slot) => Target + slot;
    public byte* Bank(int index = 0, int slot = 0) => (byte*)Actor(slot) + 0x774 + index * 0x2D8;
    public int? ActiveCommitAttacker(int slot) => _nativeDamageCommitAttacker[slot];

    private sealed class CommitRuntime
    {
        public bool AwaitingTurnEnd = true, ParryWindowActive = false;
        public byte CurrentAttackerId = 22, CurrentCueIndex = 0;
        public uint CurrentPartyTargetMask = 1, LastParriedTargetMask;
        public ulong CueFirstSeenFrame = 0;
        public bool TurnImpactMissedSeen = false;
    }

    private sealed class CalculationTelemetry
    {
        public bool CalcDamageFired, CalcDamageIntercepted, SetMotionFired, SetDamageTargetFired;
        public int CommandId;
        public uint HpBeforeFinalization, HpAfterFinalization;
    }

    private sealed class TestBattleAdapter
    {
        private readonly Chr* _party = (Chr*)NativeMemory.AllocZeroed((nuint)(sizeof(Chr) * PartyActorCapacity));
        public Chr* GetPlayerCharacters() => _party;
    }

    public ParryModule()
    {
        Target->stat_exist_flag = true;
        Target->ram.hp = 1000;
        Bank(0)[2] = Bank(1)[2] = 255;
    }

    public void Dispose() => NativeMemory.Free(Target);

    public void Stage(int bank, params int[] hp)
    {
        byte* b = Bank(bank);
        b[0] = 0;
        b[1] = checked((byte)hp.Length);
        b[2] = 22;
        b[3] = 7;
        _payloads[bank] = hp;
        Target->damage_hp = hp[0];
    }

    private readonly int[][] _payloads = [[], []];
    public int Commit(int flags, int attacker = 22, byte command = 7, int slot = 0)
        => h_ms_set_damage_internal(attacker, command, slot, 0, flags);
    public int Dispatch(int flags, byte attacker = 22, int target = -5)
        => h_ms_set_damage(attacker, target, flags);
    private int orig_ms_set_damage(byte attacker, int target, int flags)
    {
        NativeDispatchCalls++;
        return Commit(flags, attacker, slot: target >= 0 ? target : 0);
    }
    private void log_hook_ms_set_damage(byte attacker, int target, int flags, int result) { }
    private void end_parry_window(string reason) => WindowOpen = false;
    public void Motion(byte target = 0) => h_ms_damage_set_motion(target, 5, 1);
    private void orig_ms_damage_set_motion(byte target, int p2, int p3) => NativeMotionCalls++;
    private bool parry_block_recently_played(byte target) => false;
    public bool OuterCommitWouldSkip(bool resolved, bool committedBeforeWindow)
        => should_skip_set_damage_for_parry(0, Target, resolved, committedBeforeWindow);
    public int Calculate(bool inner, int attacker, int slot = 0)
        => inner
            ? h_ms_calc_damage_internal(attacker, 0, slot, (nint)Target, 0, 7, 0, 0, 0, 0, 1)
            : h_ms_calc_damage(attacker, 0, slot, (nint)Target, 0, 7, 0, 0, 0, 0, 1);
    private int orig_ms_calc_damage(int attacker, nint user, int slot, nint target, nint command, int id, nint p7, nint p8, nint p9, nint p10, int p11)
    {
        OriginalCalls++;
        return ChainInnerCalculation
            ? h_ms_calc_damage_internal(attacker, user, slot, target, command, id, p7, p8, p9, p10, p11)
            : 31;
    }
    private int orig_ms_calc_damage_internal(int attacker, nint user, int slot, nint target, nint command, int id, nint p7, nint p8, nint p9, nint p10, int p11)
    { OriginalCalls++; return 32; }
    private void log_hook_ms_calc_damage(int attacker, int target, int command, int hits, int result, nint pointer) { }

    private int orig_ms_set_damage_internal(int attacker, byte command, int slot, int p4, int flags)
    {
        OriginalCalls++;
        Chr* target = Actor(slot);
        int result = 0;
        for (int i = 0; i < 2; i++)
        {
            byte* b = Bank(i, slot);
            if (b[2] != attacker || b[3] != command) continue;
            if (b[0] < b[1] && (flags & 8) == 0)
            {
                target->ram.hp -= _payloads[i][b[0]];
                target->damage_hp = _payloads[i][b[0]];
                b[0]++;
                result |= 2;
            }
            if (b[0] < b[1]) result |= 1;
            else if ((flags & 0x400) != 0) b[2] = 255;
        }
        DuringNativeCommit?.Invoke();
        if (NativeCommitEmitsMotion) Motion((byte)slot);
        return result;
    }

    private bool is_parry_slot_window_open(int slot) => WindowOpen;
    private bool try_get_enemy_attack_cue(out AttackCue cue, out byte index, out Chr* actor)
    { cue = default; index = 0; actor = null; return true; }
    private uint extract_party_target_mask(AttackCue cue) => _runtime.CurrentPartyTargetMask;
    private void resolve_successful_parry(int slot, Chr* target, string source, bool closeWindow)
    { Successes++; _runtime.LastParriedTargetMask |= 1u << slot; }
    private void negate_damage_on_impact(Chr* target)
    { target->damage_hp = target->damage_mp = target->damage_ctb = 0; }
    private void mark_dodge_resolved(int slot) => _dodgeResolvedAtImpactMask |= 1u << slot;
    private bool is_dodge_window_valid() => _dodgeWindowActive;
    private int next_label_seed() => 1;
    private string format_actor_slot(byte slot) => slot.ToString();
    private void log_debug(string text) { }
    private void write_session_hook_entry(string text) { }
    private ValidationCall? begin_native_validation(string stage, int attacker, int slot, int command, int flags = -1, int batch = -1, nint target = 0) => null;
    private sealed class ValidationCall : IDisposable
    {
        public bool Completed;
        public void Dispose() { }
    }
}
