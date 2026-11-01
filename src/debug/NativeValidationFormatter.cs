using System.Text.Json;

namespace Fahrenheit.Mods.Parry;

internal readonly record struct NativeValidationBank(byte Cursor, byte Count, byte Attacker, byte CommandIndex);

internal readonly record struct NativeValidationActor(bool ReadOk, int Hp, int DamageHp, int WillDie,
    int Death, int DeathLatch, uint Status, int StatusBits, int Stone, int SleepTurns,
    NativeValidationBank Bank0, NativeValidationBank Bank1);

internal readonly record struct NativeValidationRecord(long Sequence, long Qpc, long QpcFrequency, int ThreadId,
    ulong SimulationTick, long CallbackId, long ParentId, string Stage, string Phase, string Outcome,
    int OriginalCalls, int Attacker, int Slot, int CommandId, int CommandIndex, int MotionMode, int PhaseFlags, int BatchHitCount,
    long WindowGeneration, ulong WindowStartTick, ulong WindowDeadlineTick, ulong GateDeadlineTick, bool GateOpen,
    ParryInputState InputState, int CueAttacker, int ArmedAttacker, byte CueIndex, ulong CueFirstSeenTick, uint CueSignature, uint TargetMask,
    uint ParriedMask, uint InternalMask, uint ImpactMask, uint DodgeMask, NativeValidationActor Actor);

internal static class NativeValidationFormatter
{
    internal const int MaximumRecords = 12000;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static bool IsEnabled(string? request, bool logging)
        => request == "1" && logging;

    internal static string Format(in NativeValidationRecord record)
        => "[native-validation] " + JsonSerializer.Serialize(record, Options);
}
