namespace Fahrenheit.Mods.Parry;

public enum TurnTimelineRuntimeSignalKind
{
    CueSnapshot,
    DispatchStarted,
    DispatchConsumed,
    DamageResolved,
    ParryWindowOpened,
    ParrySucceeded,
    ParryMissed,
    QueueFlushed
}

public readonly record struct TurnTimelineRuntimeSignal(
    TurnTimelineRuntimeSignalKind Kind,
    DateTime TimestampLocal,
    ulong FrameIndex,
    IReadOnlyList<TurnTimelineCueObservation>? Cues = null,
    int CueTurnId = 0,
    bool ParryWindowActive = false,
    byte AttackerId = 0,
    int QueueIndex = -1,
    int TargetSlot = -1,
    ushort CommandId = 0,
    string CommandLabel = "",
    string SourceStage = "",
    string Reason = "");

public sealed class TurnTimelineRuntimeEventSource
{
    private readonly List<TurnTimelineRuntimeSignal> _pending = new(128);

    public int PendingCount => _pending.Count;

    public void EmitCueSnapshot(
        IReadOnlyList<TurnTimelineCueObservation> cues,
        int cueTurnId,
        bool parryWindowActive,
        DateTime timestampLocal,
        ulong frameIndex)
    {
        // Copy snapshot now so later frame mutations don't affect queued signal payload.
        var copy = new List<TurnTimelineCueObservation>(cues.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            copy.Add(cues[i]);
        }

        _pending.Add(new TurnTimelineRuntimeSignal(
            Kind: TurnTimelineRuntimeSignalKind.CueSnapshot,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex,
            Cues: copy,
            CueTurnId: cueTurnId,
            ParryWindowActive: parryWindowActive));
    }

    public void EmitDispatchStarted(byte attackerId, int queueIndex, DateTime timestampLocal, ulong frameIndex, bool parryWindowActive)
    {
        if (is_duplicate_dispatch_signal(TurnTimelineRuntimeSignalKind.DispatchStarted, attackerId, queueIndex, frameIndex))
        {
            return;
        }

        _pending.Add(new TurnTimelineRuntimeSignal(
            Kind: TurnTimelineRuntimeSignalKind.DispatchStarted,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex,
            AttackerId: attackerId,
            QueueIndex: queueIndex,
            ParryWindowActive: parryWindowActive));
    }

    public void EmitDispatchConsumed(byte attackerId, int queueIndex, DateTime timestampLocal, ulong frameIndex, string reason)
    {
        if (is_duplicate_dispatch_signal(TurnTimelineRuntimeSignalKind.DispatchConsumed, attackerId, queueIndex, frameIndex))
        {
            return;
        }

        _pending.Add(new TurnTimelineRuntimeSignal(
            Kind: TurnTimelineRuntimeSignalKind.DispatchConsumed,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex,
            AttackerId: attackerId,
            QueueIndex: queueIndex,
            Reason: reason ?? string.Empty));
    }

    public void EmitDamageResolved(
        int targetSlot,
        DateTime timestampLocal,
        ulong frameIndex,
        byte attackerId = 0,
        int queueIndex = -1,
        ushort commandId = 0,
        string commandLabel = "",
        string sourceStage = "")
    {
        _pending.Add(new TurnTimelineRuntimeSignal(
            Kind: TurnTimelineRuntimeSignalKind.DamageResolved,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex,
            AttackerId: attackerId,
            QueueIndex: queueIndex,
            TargetSlot: targetSlot,
            CommandId: commandId,
            CommandLabel: commandLabel ?? string.Empty,
            SourceStage: sourceStage ?? string.Empty));
    }

    public void EmitParryWindowOpened(DateTime timestampLocal, ulong frameIndex)
    {
        _pending.Add(new TurnTimelineRuntimeSignal(
            Kind: TurnTimelineRuntimeSignalKind.ParryWindowOpened,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex));
    }

    public void EmitParrySucceeded(DateTime timestampLocal, ulong frameIndex)
    {
        _pending.Add(new TurnTimelineRuntimeSignal(
            Kind: TurnTimelineRuntimeSignalKind.ParrySucceeded,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex));
    }

    public void EmitParryMissed(DateTime timestampLocal, ulong frameIndex, string reason)
    {
        _pending.Add(new TurnTimelineRuntimeSignal(
            Kind: TurnTimelineRuntimeSignalKind.ParryMissed,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex,
            Reason: reason ?? string.Empty));
    }

    public void EmitQueueFlushed(int cueTurnId, DateTime timestampLocal, ulong frameIndex)
    {
        _pending.Add(new TurnTimelineRuntimeSignal(
            Kind: TurnTimelineRuntimeSignalKind.QueueFlushed,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex,
            CueTurnId: cueTurnId));
    }

    public void Drain(List<TurnTimelineRuntimeSignal> destination)
    {
        destination.AddRange(_pending);
        _pending.Clear();
    }

    private bool is_duplicate_dispatch_signal(TurnTimelineRuntimeSignalKind kind, byte attackerId, int queueIndex, ulong frameIndex)
    {
        if (_pending.Count == 0) return false;
        TurnTimelineRuntimeSignal last = _pending[^1];
        return last.Kind == kind
            && last.AttackerId == attackerId
            && last.QueueIndex == queueIndex
            && last.FrameIndex == frameIndex;
    }
}
