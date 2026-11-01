namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private readonly string? _nativeValidationRequest = Environment.GetEnvironmentVariable("FFX_PARRY_NATIVE_VALIDATION");
    private readonly object _nativeValidationGate = new();
    private long _nativeValidationSequence, _nativeValidationCallback, _nativeValidationGeneration;
    private readonly ulong[] _nativeValidationWindowStart = new ulong[PartyActorCapacity];
    private readonly ulong[] _nativeValidationWindowDeadline = new ulong[PartyActorCapacity];
    private readonly long[] _nativeValidationWindowGeneration = new long[PartyActorCapacity];
    [ThreadStatic] private static NativeValidationCall? _nativeValidationCurrent;

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool native_validation_read(nint process, nint address, void* destination, nuint size, out nuint copied);

    private sealed class NativeValidationCall : IDisposable
    {
        internal readonly ParryModule Module;
        internal readonly NativeValidationCall? Parent;
        internal readonly string Stage;
        internal readonly int Attacker, Slot, Command, Flags, Batch;
        internal readonly nint Target;
        internal readonly long Id;
        internal int OriginalCalls;
        internal bool Completed;

        internal NativeValidationCall(ParryModule module, string stage, int attacker, int slot, int command, int flags, int batch, nint target)
        {
            Module = module; Stage = stage; Attacker = attacker; Slot = slot; Command = command;
            Flags = flags; Batch = batch; Target = target; Parent = _nativeValidationCurrent;
            Id = System.Threading.Interlocked.Increment(ref module._nativeValidationCallback);
        }

        public void Dispose()
        {
            try { Module.emit_native_validation(this, "post", !Completed ? "incomplete" : Stage is not ("calc" or "calc_internal" or "commit" or "motion") ? "observed" : OriginalCalls == 0 ? "blocked" : "passed"); }
            catch { }
            finally { _nativeValidationCurrent = Parent; }
        }
    }

    private NativeValidationCall? begin_native_validation(string stage, int attacker, int slot, int command, int flags = -1, int batch = -1, nint target = 0)
    {
        if (!NativeValidationFormatter.IsEnabled(_nativeValidationRequest, _optionLogging)
            || _sessionLogDisabled || _sessionDebugLogWriter is null
            || _nativeValidationSequence >= NativeValidationFormatter.MaximumRecords
            || ((uint)slot >= PartyActorCapacity && slot != -1)) return null;
        NativeValidationCall? previous = _nativeValidationCurrent;
        try
        {
            if (target == 0 && slot >= 0)
            {
                Chr* party = _battleAdapter.GetPlayerCharacters();
                if (party != null) target = (nint)(party + slot);
            }
            var call = new NativeValidationCall(this, stage, attacker, slot, command, flags, batch, target);
            _nativeValidationCurrent = call;
            emit_native_validation(call, "pre", "pending");
            return call;
        }
        catch { _nativeValidationCurrent = previous; return null; }
    }

    private void native_validation_original(string stage)
    {
        NativeValidationCall? call = _nativeValidationCurrent;
        if (call?.Module == this && call.Stage == stage) call.OriginalCalls++;
    }

    private void emit_native_validation(NativeValidationCall call, string phase, string outcome)
    {
        try
        {
            lock (_nativeValidationGate)
            {
                if (_nativeValidationSequence >= NativeValidationFormatter.MaximumRecords) return;
                long qpc = Stopwatch.GetTimestamp();
                NativeValidationActor actor = default;
                Chr copy = default;
                if (call.Target != 0 && native_validation_read(-1, call.Target, &copy, (nuint)sizeof(Chr), out nuint copied)
                    && copied == (nuint)sizeof(Chr))
                {
                    byte* bytes = (byte*)&copy;
                    NativeValidationBank ReadBank(int offset) => new(bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3]);
                    actor = new(true, copy.ram.hp, copy.damage_hp, copy.stat_will_die, copy.stat_death,
                        bytes[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch], (uint)copy.ram.status_suffer,
                        *(ushort*)(bytes + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits), copy.stat_stone,
                        copy.ram.status_suffer_turns_left.sleep,
                        ReadBank(ExternalMemoryOffsetMap.ChrStruct.DamageBank0Header), ReadBank(ExternalMemoryOffsetMap.ChrStruct.DamageBank1Header));
                }
                int slot = call.Slot;
                var record = new NativeValidationRecord(++_nativeValidationSequence, qpc, Stopwatch.Frequency,
                    Environment.CurrentManagedThreadId, _debugFrameIndex, call.Id, call.Parent?.Id ?? 0,
                    call.Stage, phase, outcome, call.OriginalCalls, call.Attacker, slot,
                    call.Stage is "calc" or "calc_internal" ? call.Command : -1,
                    call.Stage == "commit" ? call.Command : -1, call.Stage == "motion" ? call.Command : -1, call.Flags, call.Batch,
                    slot >= 0 ? _nativeValidationWindowGeneration[slot] : _nativeValidationGeneration,
                    slot >= 0 ? _nativeValidationWindowStart[slot] : 0, slot >= 0 ? _nativeValidationWindowDeadline[slot] : 0,
                    slot >= 0 ? _parryExpiryTick[slot] : 0, slot >= 0 && is_parry_slot_window_open(slot),
                    _runtime.InputState, _runtime.CurrentAttackerId, slot >= 0 ? _parryArmedAttackerId[slot] : -1, _runtime.CurrentCueIndex, _runtime.CueFirstSeenFrame,
                    _runtime.CurrentCueSignature, _runtime.CurrentPartyTargetMask, _runtime.LastParriedTargetMask,
                    _internalInterceptedMask, _parryResolvedAtImpactMask, _dodgeResolvedAtImpactMask, actor);
                write_session_hook_entry(NativeValidationFormatter.Format(record));
            }
        }
        catch { }
    }

    private void trace_native_validation_lifecycle(string stage, string? reason = null)
    {
        if (!NativeValidationFormatter.IsEnabled(_nativeValidationRequest, _optionLogging)) return;
        try
        {
            if (stage == "open")
            {
                _nativeValidationGeneration++;
                for (int slot = 0; slot < PartyActorCapacity; slot++)
                    if ((_runtime.CurrentPartyTargetMask & (1u << slot)) != 0)
                    {
                        _nativeValidationWindowStart[slot] = _runtime.WindowOpenFrame;
                        _nativeValidationWindowDeadline[slot] = _parryExpiryTick[slot];
                        _nativeValidationWindowGeneration[slot] = _nativeValidationGeneration;
                    }
            }
            string label = reason is null ? stage : stage + ":" + reason;
            if (_runtime.CurrentPartyTargetMask == 0)
            {
                using var empty = begin_native_validation(label, -1, -1, -1);
                if (empty is not null) empty.Completed = true;
            }
            for (int slot = 0; slot < PartyActorCapacity; slot++)
            {
                if ((_runtime.CurrentPartyTargetMask & (1u << slot)) == 0) continue;
                using var call = begin_native_validation(label, -1, slot, -1);
                if (call is not null) call.Completed = true;
            }
        }
        catch { }
    }
}
