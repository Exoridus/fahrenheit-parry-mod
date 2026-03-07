namespace Fahrenheit.Mods.Parry;

public enum TurnTimelineLifecycleState
{
    Pending,
    Active,
    Completed
}

public enum TurnTimelineParryState
{
    Pending,
    Waiting,
    Open,
    Parried,
    Missed,
    None
}

public enum TurnTimelineParryability
{
    Parryable,
    Unknown,
    NonParryable
}

public enum TurnTimelineCommandConfidence
{
    None,
    Low,
    Medium,
    High
}

public enum TurnTimelineEventKind
{
    CueSnapshot,
    CueAdded,
    CueUpdated,
    CueRemoved,
    DispatchStarted,
    DispatchConsumed,
    DamageResolved,
    QueueFlushed,
    LifecycleChanged,
    ParryStateChanged,
    ParryabilityChanged,
    IntegrityWarning
}

public readonly record struct TurnTimelineCueFingerprint(
    byte AttackerId,
    int CommandCount,
    uint CommandSignature,
    uint PartyMask,
    uint NonPartyMask,
    bool IsEnemy,
    bool IsMagic);

public readonly record struct TurnTimelineCueObservation(
    int QueueIndex,
    byte AttackerId,
    string Actor,
    string Action,
    string Targets,
    TurnTimelineParryability Parryability,
    TurnTimelineCommandInfo Command,
    TurnTimelineCueFingerprint Fingerprint);

public readonly record struct TurnTimelineCommandInfo(
    ushort CommandId,
    string Label,
    string Kind,
    string Source,
    TurnTimelineCommandConfidence Confidence)
{
    public static TurnTimelineCommandInfo Empty => new(
        CommandId: 0,
        Label: string.Empty,
        Kind: string.Empty,
        Source: "none",
        Confidence: TurnTimelineCommandConfidence.None);
}

public sealed class TurnTimelineRow
{
    public int RowId { get; init; }
    public int TurnId { get; set; }
    public int TurnOrdinal { get; set; }
    public DateTime TimestampLocal { get; set; }
    public ulong FrameIndex { get; set; }
    public byte AttackerId { get; set; }
    public string Actor { get; set; } = "-";
    public string Action { get; set; } = "System";
    public string Targets { get; set; } = "-";
    public TurnTimelineLifecycleState Lifecycle { get; set; }
    public TurnTimelineParryState ParryState { get; set; }
    public TurnTimelineParryability Parryability { get; set; }
    public TurnTimelineCommandInfo Command { get; set; } = TurnTimelineCommandInfo.Empty;
    public int QueuePosition { get; set; }
    public int QueueTotal { get; set; }
    public bool IsFlushMarker { get; set; }
    public bool IsDiagnosticMarker { get; set; }
    public string DiagnosticText { get; set; } = string.Empty;
    internal TurnTimelineCueKey CueKey { get; set; }
}

public readonly record struct TurnTimelineEvent(
    TurnTimelineEventKind Kind,
    int RowId,
    DateTime TimestampLocal,
    ulong FrameIndex,
    string Message);

internal readonly record struct TurnTimelineCueKey(
    TurnTimelineCueFingerprint Fingerprint,
    int InstanceSequence);

public sealed class TurnTimelineTracker
{
    private readonly FixedRingBuffer<TurnTimelineRow> _rows;
    private readonly Dictionary<int, TurnTimelineRow> _rowsById = new();
    private readonly List<int> _activeRowOrder = new();
    private readonly List<int> _activeRowScratch = new();
    private readonly List<TurnTimelineEvent> _pendingEvents = new();

    private int _nextRowId = 1;
    private int _nextCueInstanceSequence = 1;
    private int _turnOrdinalInTurn;
    private int _currentTurnId;

    public TurnTimelineTracker(int rowCapacity)
    {
        _rows = new FixedRingBuffer<TurnTimelineRow>(Math.Max(16, rowCapacity));
    }

    public int RowCount => _rows.Count;
    public int Capacity => _rows.Capacity;
    public TurnTimelineRow GetRowAt(int index) => _rows[index];
    public bool TryGetRowById(int rowId, out TurnTimelineRow? row) => _rowsById.TryGetValue(rowId, out row);

    public void BeginBattle()
    {
        _activeRowOrder.Clear();
        _activeRowScratch.Clear();
        _turnOrdinalInTurn = 0;
    }

    public void EndBattle()
    {
        _activeRowOrder.Clear();
        _activeRowScratch.Clear();
        _turnOrdinalInTurn = 0;
    }

    public void UpdateCues(
        IReadOnlyList<TurnTimelineCueObservation> cues,
        int cueTurnId,
        DateTime timestampLocal,
        ulong frameIndex,
        bool parryWindowActive)
    {
        _pendingEvents.Add(new TurnTimelineEvent(
            Kind: TurnTimelineEventKind.CueSnapshot,
            RowId: 0,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex,
            Message: $"Cue snapshot observed ({cues.Count} cue(s))."));

        if (cues.Count > 0 && cueTurnId != _currentTurnId)
        {
            _currentTurnId = cueTurnId;
            _turnOrdinalInTurn = 0;
        }

        _activeRowScratch.Clear();

        HashSet<int> matched = new(_activeRowOrder.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            TurnTimelineCueObservation cue = cues[i];
            int rowId = find_matching_row_id(cue, matched);
            if (rowId >= 0)
            {
                matched.Add(rowId);
                TurnTimelineRow existing = _rowsById[rowId];
                TurnTimelineParryability previousParryability = existing.Parryability;
                TurnTimelineCueFingerprint previousFingerprint = existing.CueKey.Fingerprint;
                bool changed = existing.AttackerId != cue.AttackerId
                    || !string.Equals(existing.Actor, cue.Actor, StringComparison.Ordinal)
                    || !string.Equals(existing.Action, cue.Action, StringComparison.Ordinal)
                    || !string.Equals(existing.Targets, cue.Targets, StringComparison.Ordinal)
                    || existing.Parryability != cue.Parryability
                    || existing.Command.CommandId != cue.Command.CommandId
                    || existing.Command.Confidence != cue.Command.Confidence
                    || !previousFingerprint.Equals(cue.Fingerprint);
                update_row_from_cue(existing, cue, timestampLocal, frameIndex);
                if (previousParryability != cue.Parryability)
                {
                    emit_event(
                        TurnTimelineEventKind.ParryabilityChanged,
                        existing,
                        timestampLocal,
                        frameIndex,
                        $"Turn {format_turn_id(existing)} parryability {format_parryability(previousParryability)} => {format_parryability(cue.Parryability)}.");
                }
                if (changed)
                {
                    emit_event(TurnTimelineEventKind.CueUpdated, existing, timestampLocal, frameIndex, $"Turn {format_turn_id(existing)} cue updated.");
                }
            }
            else
            {
                TurnTimelineRow added = create_cue_row(cue, cueTurnId, timestampLocal, frameIndex);
                rowId = added.RowId;
                emit_event(TurnTimelineEventKind.CueAdded, added, timestampLocal, frameIndex, $"Turn {format_turn_id(added)} cue added ({added.Actor} {added.Action}).");
            }

            _activeRowScratch.Add(rowId);
        }

        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            int rowId = _activeRowOrder[i];
            if (matched.Contains(rowId)) continue;
            if (!_rowsById.TryGetValue(rowId, out TurnTimelineRow? removed)) continue;
            if (removed.Lifecycle == TurnTimelineLifecycleState.Completed) continue;

            complete_row(removed, timestampLocal, frameIndex, "Consumed", asMissedWhenParryable: true);
        }

        _activeRowOrder.Clear();
        _activeRowOrder.AddRange(_activeRowScratch);

        int firstActiveIndex = find_first_active_index();
        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            if (!_rowsById.TryGetValue(_activeRowOrder[i], out TurnTimelineRow? row)) continue;
            if (row.Lifecycle == TurnTimelineLifecycleState.Completed) continue;

            row.QueuePosition = i + 1;
            row.QueueTotal = _activeRowOrder.Count;

            TurnTimelineLifecycleState desiredLifecycle = i == firstActiveIndex
                ? TurnTimelineLifecycleState.Active
                : TurnTimelineLifecycleState.Pending;
            set_lifecycle(row, desiredLifecycle, timestampLocal, frameIndex);

            TurnTimelineParryState desiredParry = compute_desired_parry_state(row, parryWindowActive);
            set_parry(row, desiredParry, timestampLocal, frameIndex);
        }

        validate_integrity(timestampLocal, frameIndex);
    }

    public void CorrelateDispatchStarted(
        byte attackerId,
        int queueIndex,
        DateTime timestampLocal,
        ulong frameIndex,
        bool parryWindowActive)
    {
        TurnTimelineRow? row = find_best_row_for_dispatch(attackerId, queueIndex);
        if (row == null)
        {
            append_integrity_warning($"Dispatch started could not be correlated (attacker={attackerId}, q={queueIndex}).", timestampLocal, frameIndex);
            return;
        }

        if (queueIndex >= 0)
        {
            int desiredQueuePos = queueIndex + 1;
            if (row.QueuePosition > 0 && row.QueuePosition != desiredQueuePos)
            {
                append_integrity_warning(
                    $"Dispatch correlation mismatch for {row.Actor}: expected q{desiredQueuePos}, matched q{row.QueuePosition}.",
                    timestampLocal,
                    frameIndex);
            }
        }

        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            int rowId = _activeRowOrder[i];
            if (rowId == row.RowId) continue;
            if (!_rowsById.TryGetValue(rowId, out TurnTimelineRow? other)) continue;
            if (other.Lifecycle == TurnTimelineLifecycleState.Active)
            {
                set_lifecycle(other, TurnTimelineLifecycleState.Pending, timestampLocal, frameIndex);
            }
        }

        set_lifecycle(row, TurnTimelineLifecycleState.Active, timestampLocal, frameIndex);
        set_parry(row, compute_desired_parry_state(row, parryWindowActive), timestampLocal, frameIndex);
        emit_event(
            TurnTimelineEventKind.DispatchStarted,
            row,
            timestampLocal,
            frameIndex,
            $"Dispatch started for {row.Actor} (q{queueIndex}).");
        validate_integrity(timestampLocal, frameIndex);
    }

    public void CorrelateDispatchConsumed(
        byte attackerId,
        int queueIndex,
        DateTime timestampLocal,
        ulong frameIndex,
        string reason)
    {
        TurnTimelineRow? row = find_consumed_row(attackerId, queueIndex);
        if (row == null)
        {
            append_integrity_warning($"Dispatch consumed could not be correlated (attacker={attackerId}, q={queueIndex}).", timestampLocal, frameIndex);
            return;
        }

        complete_row(row, timestampLocal, frameIndex, reason, asMissedWhenParryable: row.Parryability == TurnTimelineParryability.Parryable);
        emit_event(
            TurnTimelineEventKind.DispatchConsumed,
            row,
            timestampLocal,
            frameIndex,
            $"Dispatch consumed for {row.Actor} (q{queueIndex}, {reason}).");
        promote_next_pending(timestampLocal, frameIndex, parryWindowActive: false);
        validate_integrity(timestampLocal, frameIndex);
    }

    public void CorrelateDamageResolved(
        int targetSlot,
        DateTime timestampLocal,
        ulong frameIndex,
        byte attackerId = 0,
        int queueIndex = -1,
        ushort commandId = 0,
        string commandLabel = "",
        string sourceStage = "",
        string targetLabel = "")
    {
        TurnTimelineRow? active = find_damage_row(attackerId, queueIndex);
        if (active == null) return;

        string source = string.IsNullOrWhiteSpace(sourceStage) ? "impact" : sourceStage;
        string resolvedTarget = string.IsNullOrWhiteSpace(targetLabel)
            ? $"slot {targetSlot}"
            : targetLabel;
        string commandSummary = commandId == 0
            ? string.Empty
            : string.IsNullOrWhiteSpace(commandLabel)
                ? $" (cmd 0x{commandId:X4})"
                : $" (cmd 0x{commandId:X4} {commandLabel})";

        emit_event(
            TurnTimelineEventKind.DamageResolved,
            active,
            timestampLocal,
            frameIndex,
            $"Damage resolved [{source}] on {resolvedTarget} while {active.Actor} is active{commandSummary}.");
    }

    public void MarkActiveParryOpen(DateTime timestampLocal, ulong frameIndex)
    {
        TurnTimelineRow? active = find_first_active_row();
        if (active == null || active.Parryability == TurnTimelineParryability.NonParryable) return;
        set_parry(active, TurnTimelineParryState.Open, timestampLocal, frameIndex);
    }

    public void MarkActiveParried(DateTime timestampLocal, ulong frameIndex)
    {
        TurnTimelineRow? active = find_first_active_row();
        if (active == null || active.Parryability == TurnTimelineParryability.NonParryable) return;

        set_parry(active, TurnTimelineParryState.Parried, timestampLocal, frameIndex);
        set_lifecycle(active, TurnTimelineLifecycleState.Completed, timestampLocal, frameIndex);
        promote_next_pending(timestampLocal, frameIndex, parryWindowActive: false);
        validate_integrity(timestampLocal, frameIndex);
    }

    public void MarkActiveMissed(string reason, DateTime timestampLocal, ulong frameIndex)
    {
        TurnTimelineRow? active = find_first_active_row();
        if (active == null || active.Parryability == TurnTimelineParryability.NonParryable) return;

        set_parry(active, TurnTimelineParryState.Missed, timestampLocal, frameIndex);
        set_lifecycle(active, TurnTimelineLifecycleState.Completed, timestampLocal, frameIndex);
        emit_event(TurnTimelineEventKind.ParryStateChanged, active, timestampLocal, frameIndex, $"Turn {format_turn_id(active)} missed ({reason}).");
        promote_next_pending(timestampLocal, frameIndex, parryWindowActive: false);
        validate_integrity(timestampLocal, frameIndex);
    }

    public void AppendFlushMarker(int cueTurnId, DateTime timestampLocal, ulong frameIndex)
    {
        var row = new TurnTimelineRow
        {
            RowId = _nextRowId++,
            TurnId = cueTurnId,
            TurnOrdinal = 0,
            TimestampLocal = timestampLocal,
            FrameIndex = frameIndex,
            Actor = "Queue Flush",
            Action = "System",
            Targets = "-",
            Lifecycle = TurnTimelineLifecycleState.Completed,
            ParryState = TurnTimelineParryState.None,
            Parryability = TurnTimelineParryability.NonParryable,
            IsFlushMarker = true
        };

        append_row(row);
        emit_event(TurnTimelineEventKind.QueueFlushed, row, timestampLocal, frameIndex, "Turn queue flushed.");
        _activeRowOrder.Clear();
        validate_integrity(timestampLocal, frameIndex);
    }

    public void DrainEvents(List<TurnTimelineEvent> destination)
    {
        destination.AddRange(_pendingEvents);
        _pendingEvents.Clear();
    }

    private int find_matching_row_id(TurnTimelineCueObservation cue, HashSet<int> matched)
    {
        int desiredQueuePos = cue.QueueIndex + 1;
        TurnTimelineRow? bestExactFingerprint = null;
        TurnTimelineRow? bestAttackerFallback = null;
        int bestExactDist = int.MaxValue;
        int bestAttackerDist = int.MaxValue;

        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            int rowId = _activeRowOrder[i];
            if (matched.Contains(rowId)) continue;
            if (!_rowsById.TryGetValue(rowId, out TurnTimelineRow? row)) continue;
            if (row.IsFlushMarker || row.IsDiagnosticMarker) continue;
            if (row.Lifecycle == TurnTimelineLifecycleState.Completed) continue;

            int dist = Math.Abs(row.QueuePosition - desiredQueuePos);
            bool isExactFingerprint = row.CueKey.Fingerprint.Equals(cue.Fingerprint);
            if (isExactFingerprint)
            {
                if (bestExactFingerprint == null || dist < bestExactDist)
                {
                    bestExactFingerprint = row;
                    bestExactDist = dist;
                }
                continue;
            }

            if (row.AttackerId == cue.AttackerId)
            {
                if (bestAttackerFallback == null || dist < bestAttackerDist)
                {
                    bestAttackerFallback = row;
                    bestAttackerDist = dist;
                }
            }
        }

        if (bestExactFingerprint != null) return bestExactFingerprint.RowId;
        if (bestAttackerFallback != null) return bestAttackerFallback.RowId;
        return -1;
    }

    private TurnTimelineRow create_cue_row(
        TurnTimelineCueObservation cue,
        int cueTurnId,
        DateTime timestampLocal,
        ulong frameIndex)
    {
        var row = new TurnTimelineRow
        {
            RowId = _nextRowId++,
            TurnId = cueTurnId,
            TurnOrdinal = ++_turnOrdinalInTurn,
            TimestampLocal = timestampLocal,
            FrameIndex = frameIndex,
            AttackerId = cue.AttackerId,
            Actor = cue.Actor,
            Action = cue.Action,
            Targets = cue.Targets,
            Lifecycle = TurnTimelineLifecycleState.Pending,
            Parryability = cue.Parryability,
            Command = cue.Command,
            ParryState = cue.Parryability == TurnTimelineParryability.NonParryable
                ? TurnTimelineParryState.None
                : TurnTimelineParryState.Pending,
            CueKey = new TurnTimelineCueKey(cue.Fingerprint, _nextCueInstanceSequence++)
        };

        append_row(row);
        return row;
    }

    private void append_row(TurnTimelineRow row)
    {
        TurnTimelineRow? evicted = _rows.Add(row);
        if (evicted != null)
        {
            _rowsById.Remove(evicted.RowId);
            _activeRowOrder.RemoveAll(id => id == evicted.RowId);
            _activeRowScratch.RemoveAll(id => id == evicted.RowId);
        }

        _rowsById[row.RowId] = row;
    }

    private void update_row_from_cue(
        TurnTimelineRow row,
        TurnTimelineCueObservation cue,
        DateTime timestampLocal,
        ulong frameIndex)
    {
        row.TimestampLocal = timestampLocal;
        row.FrameIndex = frameIndex;
        row.AttackerId = cue.AttackerId;
        row.Actor = cue.Actor;
        row.Action = cue.Action;
        row.Targets = cue.Targets;
        row.Parryability = cue.Parryability;
        row.Command = cue.Command;
        row.CueKey = new TurnTimelineCueKey(cue.Fingerprint, row.CueKey.InstanceSequence);
    }

    private void complete_row(
        TurnTimelineRow row,
        DateTime timestampLocal,
        ulong frameIndex,
        string reason,
        bool asMissedWhenParryable)
    {
        if (row.Lifecycle == TurnTimelineLifecycleState.Completed) return;

        if (row.Parryability == TurnTimelineParryability.NonParryable)
        {
            set_parry(row, TurnTimelineParryState.None, timestampLocal, frameIndex);
        }
        else if (row.Parryability == TurnTimelineParryability.Parryable
            && asMissedWhenParryable
            && row.ParryState != TurnTimelineParryState.Parried
            && row.ParryState != TurnTimelineParryState.Missed)
        {
            set_parry(row, TurnTimelineParryState.Missed, timestampLocal, frameIndex);
        }

        set_lifecycle(row, TurnTimelineLifecycleState.Completed, timestampLocal, frameIndex);
        emit_event(TurnTimelineEventKind.CueRemoved, row, timestampLocal, frameIndex, $"Turn {format_turn_id(row)} completed ({reason}).");
    }

    private void promote_next_pending(DateTime timestampLocal, ulong frameIndex, bool parryWindowActive)
    {
        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            int rowId = _activeRowOrder[i];
            if (!_rowsById.TryGetValue(rowId, out TurnTimelineRow? row)) continue;
            if (row.Lifecycle == TurnTimelineLifecycleState.Completed) continue;

            row.QueuePosition = i + 1;
            row.QueueTotal = _activeRowOrder.Count;
            set_lifecycle(row, TurnTimelineLifecycleState.Active, timestampLocal, frameIndex);
            TurnTimelineParryState desired = compute_desired_parry_state(row, parryWindowActive);
            set_parry(row, desired, timestampLocal, frameIndex);
            break;
        }
    }

    private int find_first_active_index()
    {
        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            int rowId = _activeRowOrder[i];
            if (!_rowsById.TryGetValue(rowId, out TurnTimelineRow? row)) continue;
            if (row.Lifecycle != TurnTimelineLifecycleState.Completed) return i;
        }

        return -1;
    }

    private TurnTimelineRow? find_first_active_row()
    {
        int idx = find_first_active_index();
        if (idx < 0 || idx >= _activeRowOrder.Count) return null;
        int rowId = _activeRowOrder[idx];
        return _rowsById.TryGetValue(rowId, out TurnTimelineRow? row) ? row : null;
    }

    private TurnTimelineRow? find_damage_row(byte attackerId, int queueIndex)
    {
        if (attackerId != 0 || queueIndex >= 0)
        {
            TurnTimelineRow? contextual = find_consumed_row(attackerId, queueIndex);
            if (contextual != null) return contextual;
        }

        return find_first_active_row();
    }

    private TurnTimelineRow? find_best_row_for_dispatch(byte attackerId, int queueIndex)
    {
        TurnTimelineRow? byQueue = null;
        TurnTimelineRow? byAttackerPending = null;
        TurnTimelineRow? byAttackerAny = null;

        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            int rowId = _activeRowOrder[i];
            if (!_rowsById.TryGetValue(rowId, out TurnTimelineRow? row)) continue;
            if (row.IsFlushMarker || row.IsDiagnosticMarker) continue;
            if (row.Lifecycle == TurnTimelineLifecycleState.Completed) continue;

            if (row.QueuePosition == queueIndex + 1 && row.AttackerId == attackerId)
            {
                byQueue = row;
                break;
            }

            if (row.AttackerId == attackerId && row.Lifecycle == TurnTimelineLifecycleState.Pending && byAttackerPending == null)
            {
                byAttackerPending = row;
            }

            if (row.AttackerId == attackerId && byAttackerAny == null)
            {
                byAttackerAny = row;
            }
        }

        return byQueue ?? byAttackerPending ?? byAttackerAny ?? find_first_active_row();
    }

    private TurnTimelineRow? find_consumed_row(byte attackerId, int queueIndex)
    {
        int desiredQueuePos = queueIndex + 1;
        TurnTimelineRow? active = find_first_active_row();
        if (active != null && active.AttackerId == attackerId && (queueIndex < 0 || active.QueuePosition == desiredQueuePos)) return active;

        TurnTimelineRow? byQueue = null;
        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            int rowId = _activeRowOrder[i];
            if (!_rowsById.TryGetValue(rowId, out TurnTimelineRow? row)) continue;
            if (row.IsFlushMarker || row.IsDiagnosticMarker) continue;
            if (row.Lifecycle == TurnTimelineLifecycleState.Completed) continue;
            if (row.AttackerId != attackerId) continue;
            if (queueIndex >= 0 && row.QueuePosition == desiredQueuePos)
            {
                byQueue = row;
                break;
            }
        }
        if (byQueue != null) return byQueue;

        for (int i = 0; i < _activeRowOrder.Count; i++)
        {
            int rowId = _activeRowOrder[i];
            if (!_rowsById.TryGetValue(rowId, out TurnTimelineRow? row)) continue;
            if (row.IsFlushMarker || row.IsDiagnosticMarker) continue;
            if (row.Lifecycle == TurnTimelineLifecycleState.Completed) continue;
            if (row.AttackerId == attackerId) return row;
        }

        return active;
    }

    private static TurnTimelineParryState compute_desired_parry_state(TurnTimelineRow row, bool parryWindowActive)
    {
        if (row.Parryability == TurnTimelineParryability.NonParryable) return TurnTimelineParryState.None;
        if (row.Lifecycle == TurnTimelineLifecycleState.Pending) return TurnTimelineParryState.Pending;
        if (row.ParryState == TurnTimelineParryState.Parried || row.ParryState == TurnTimelineParryState.Missed) return row.ParryState;
        return parryWindowActive ? TurnTimelineParryState.Open : TurnTimelineParryState.Waiting;
    }

    private void set_lifecycle(
        TurnTimelineRow row,
        TurnTimelineLifecycleState lifecycle,
        DateTime timestampLocal,
        ulong frameIndex)
    {
        if (row.Lifecycle == lifecycle) return;
        if (!TurnTimelineStateMachine.CanTransitionLifecycle(row.Lifecycle, lifecycle))
        {
            append_integrity_warning(
                $"Illegal lifecycle transition for {format_turn_id(row)}: {format_lifecycle(row.Lifecycle, row.QueuePosition, row.QueueTotal)} => {format_lifecycle(lifecycle, row.QueuePosition, row.QueueTotal)}",
                timestampLocal,
                frameIndex);
            return;
        }

        string from = format_lifecycle(row.Lifecycle, row.QueuePosition, row.QueueTotal);
        row.Lifecycle = lifecycle;
        row.TimestampLocal = timestampLocal;
        row.FrameIndex = frameIndex;
        string to = format_lifecycle(row.Lifecycle, row.QueuePosition, row.QueueTotal);
        emit_event(TurnTimelineEventKind.LifecycleChanged, row, timestampLocal, frameIndex, $"Turn {format_turn_id(row)} lifecycle {from} => {to}.");
    }

    private void set_parry(
        TurnTimelineRow row,
        TurnTimelineParryState parryState,
        DateTime timestampLocal,
        ulong frameIndex)
    {
        if (row.ParryState == parryState) return;
        if (!TurnTimelineStateMachine.CanTransitionParry(row.Parryability, row.ParryState, parryState))
        {
            append_integrity_warning(
                $"Illegal parry transition for {format_turn_id(row)}: {format_parry(row.ParryState)} => {format_parry(parryState)}",
                timestampLocal,
                frameIndex);
            return;
        }

        string from = format_parry(row.ParryState);
        row.ParryState = parryState;
        row.TimestampLocal = timestampLocal;
        row.FrameIndex = frameIndex;
        string to = format_parry(row.ParryState);
        emit_event(TurnTimelineEventKind.ParryStateChanged, row, timestampLocal, frameIndex, $"Turn {format_turn_id(row)} parry {from} => {to}.");
    }

    private void emit_event(
        TurnTimelineEventKind kind,
        TurnTimelineRow row,
        DateTime timestampLocal,
        ulong frameIndex,
        string message)
    {
        _pendingEvents.Add(new TurnTimelineEvent(
            Kind: kind,
            RowId: row.RowId,
            TimestampLocal: timestampLocal,
            FrameIndex: frameIndex,
            Message: message));
    }

    private static string format_turn_id(TurnTimelineRow row) => $"T{row.TurnId:D3}.{row.TurnOrdinal:D2}";

    private static string format_parry(TurnTimelineParryState state) => state switch
    {
        TurnTimelineParryState.Pending => "Pending",
        TurnTimelineParryState.Waiting => "Waiting",
        TurnTimelineParryState.Open => "Open",
        TurnTimelineParryState.Parried => "Parried",
        TurnTimelineParryState.Missed => "Missed",
        _ => "-"
    };

    private static string format_parryability(TurnTimelineParryability parryability) => parryability switch
    {
        TurnTimelineParryability.Parryable => "Parryable",
        TurnTimelineParryability.Unknown => "Unknown",
        _ => "No"
    };

    private static string format_lifecycle(TurnTimelineLifecycleState state, int queuePos, int queueTotal) => state switch
    {
        TurnTimelineLifecycleState.Pending => "Pending",
        TurnTimelineLifecycleState.Active => queueTotal > 0 ? $"Active ({queuePos}/{queueTotal})" : "Active",
        _ => "Completed"
    };

    private void validate_integrity(DateTime timestampLocal, ulong frameIndex)
    {
        int activeCount = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            TurnTimelineRow row = _rows[i];
            if (row.IsFlushMarker || row.IsDiagnosticMarker) continue;
            if (row.Lifecycle == TurnTimelineLifecycleState.Active) activeCount++;
        }

        if (activeCount > 1)
        {
            append_integrity_warning($"Multiple active rows detected ({activeCount}).", timestampLocal, frameIndex);
            return;
        }

        if (activeCount == 1)
        {
            TurnTimelineRow? active = find_first_active_row();
            if (active != null && active.Lifecycle == TurnTimelineLifecycleState.Active && active.QueuePosition > 1)
            {
                append_integrity_warning($"Active row has queue position {active.QueuePosition} (>1).", timestampLocal, frameIndex);
            }
        }
    }

    private void append_integrity_warning(string text, DateTime timestampLocal, ulong frameIndex)
    {
        if (_rows.Count > 0)
        {
            TurnTimelineRow last = _rows[_rows.Count - 1];
            if (last.IsDiagnosticMarker && string.Equals(last.DiagnosticText, text, StringComparison.Ordinal) && last.FrameIndex == frameIndex)
            {
                return;
            }
        }

        var row = new TurnTimelineRow
        {
            RowId = _nextRowId++,
            TurnId = _currentTurnId,
            TurnOrdinal = 0,
            TimestampLocal = timestampLocal,
            FrameIndex = frameIndex,
            Actor = "Warning",
            Action = "Integrity",
            Targets = text,
            Lifecycle = TurnTimelineLifecycleState.Completed,
            ParryState = TurnTimelineParryState.None,
            Parryability = TurnTimelineParryability.NonParryable,
            IsDiagnosticMarker = true,
            DiagnosticText = text
        };

        append_row(row);
        emit_event(TurnTimelineEventKind.IntegrityWarning, row, timestampLocal, frameIndex, $"Timeline integrity warning: {text}");
    }

    private sealed class FixedRingBuffer<T> where T : class
    {
        private readonly T[] _items;
        private int _start;
        private int _count;

        public FixedRingBuffer(int capacity)
        {
            _items = new T[Math.Max(1, capacity)];
        }

        public int Count => _count;
        public int Capacity => _items.Length;

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
                return _items[map_index(index)];
            }
        }

        public T? Add(T value)
        {
            if (_count < _items.Length)
            {
                _items[map_index(_count)] = value;
                _count++;
                return null;
            }

            int overwriteIndex = _start;
            T evicted = _items[overwriteIndex];
            _items[overwriteIndex] = value;
            _start = (_start + 1) % _items.Length;
            return evicted;
        }

        private int map_index(int index)
        {
            return (_start + index) % _items.Length;
        }
    }
}
