namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    // Runtime turn-timeline drivers. Called from the per-tick update, not from the debug UI: one
    // watches the engine's attack-cue queue for add/remove/mutate transitions and emits the queue
    // events, the other drains those events into the turn timeline. Both are observability state
    // machines rather than debug rendering, so they live outside ParryModule.Debug.cs; the
    // formatting and history helpers they call remain there.

    private void monitor_cue_transitions()
    {
        _debugCueScratch.Clear();
        collect_live_cues(_debugCueScratch, out _);

        if (_debugCueSnapshots.Count == 0 && _debugCueScratch.Count > 0)
        {
            _debugCueTurnId++;
        }

        int maxCount = Math.Max(_debugCueSnapshots.Count, _debugCueScratch.Count);
        for (int i = 0; i < maxCount; i++)
        {
            bool hasPrev = i < _debugCueSnapshots.Count;
            bool hasCur = i < _debugCueScratch.Count;

            if (!hasPrev && hasCur)
            {
                DebugCueSnapshot added = _debugCueScratch[i];
                log_debug($"Cue+ q{added.QueueIndex}: {format_cue_brief(added)}");
                append_cue_history("ADD", added);
                continue;
            }

            if (hasPrev && !hasCur)
            {
                DebugCueSnapshot removed = _debugCueSnapshots[i];
                log_debug($"Cue- q{removed.QueueIndex}: {format_cue_brief(removed)}");
                append_cue_history("DEL", removed, "Consumed", "-");
                continue;
            }

            DebugCueSnapshot previous = _debugCueSnapshots[i];
            DebugCueSnapshot current = _debugCueScratch[i];
            if (!current.EqualsSemantic(previous))
            {
                if (is_cue_ownership_change(previous, current))
                {
                    // Queue slot ownership changed (for example party/system -> enemy/system).
                    // Treat this as replacement, not in-place mutation, to keep turn attribution clear.
                    log_debug($"Cue- q{previous.QueueIndex}: {format_cue_brief(previous)}");
                    append_cue_history("DEL", previous, "Consumed", "-");

                    log_debug($"Cue+ q{current.QueueIndex}: {format_cue_brief(current)}");
                    append_cue_history("ADD", current);
                }
                else
                {
                    log_debug($"Cue~ q{current.QueueIndex}: {format_cue_brief(previous)} -> {format_cue_brief(current)}");
                    append_cue_history("UPD", current);
                }
            }
        }

        if (_debugCueSnapshots.Count > 0 && _debugCueScratch.Count == 0)
        {
            log_debug("Cue queue flushed.");
            append_cue_flush_history();
            _turnRuntimeEvents.EmitQueueFlushed(_debugCueTurnId, current_gameplay_timestamp(), _debugFrameIndex);
        }

        sync_turn_timeline_from_cues();

        _debugCueSnapshots.Clear();
        _debugCueSnapshots.AddRange(_debugCueScratch);
    }

    private void process_turn_runtime_events()
    {
        _debugRuntimeSignalScratch.Clear();
        _turnRuntimeEvents.Drain(_debugRuntimeSignalScratch);

        for (int i = 0; i < _debugRuntimeSignalScratch.Count; i++)
        {
            TurnTimelineRuntimeSignal signal = _debugRuntimeSignalScratch[i];
            switch (signal.Kind)
            {
                case TurnTimelineRuntimeSignalKind.CueSnapshot:
                    _turnTimeline.UpdateCues(
                        cues: signal.Cues ?? Array.Empty<TurnTimelineCueObservation>(),
                        cueTurnId: signal.CueTurnId,
                        timestampLocal: signal.TimestampLocal,
                        frameIndex: signal.FrameIndex,
                        parryWindowActive: signal.ParryWindowActive);
                    break;
                case TurnTimelineRuntimeSignalKind.DispatchStarted:
                    _turnTimeline.CorrelateDispatchStarted(
                        attackerId: signal.AttackerId,
                        queueIndex: signal.QueueIndex < 0 ? 0 : signal.QueueIndex,
                        timestampLocal: signal.TimestampLocal,
                        frameIndex: signal.FrameIndex,
                        parryWindowActive: signal.ParryWindowActive);
                    break;
                case TurnTimelineRuntimeSignalKind.DispatchConsumed:
                    _turnTimeline.CorrelateDispatchConsumed(
                        attackerId: signal.AttackerId,
                        queueIndex: signal.QueueIndex,
                        timestampLocal: signal.TimestampLocal,
                        frameIndex: signal.FrameIndex,
                        reason: string.IsNullOrWhiteSpace(signal.Reason) ? "consumed" : signal.Reason);
                    break;
                case TurnTimelineRuntimeSignalKind.DamageResolved:
                    string targetLabel = signal.TargetSlot >= 0
                        ? format_actor_slot((byte)signal.TargetSlot)
                        : "Unknown target";
                    _turnTimeline.CorrelateDamageResolved(
                        targetSlot: signal.TargetSlot,
                        timestampLocal: signal.TimestampLocal,
                        frameIndex: signal.FrameIndex,
                        attackerId: signal.AttackerId,
                        queueIndex: signal.QueueIndex,
                        commandId: signal.CommandId,
                        commandLabel: signal.CommandLabel,
                        sourceStage: signal.SourceStage,
                        targetLabel: targetLabel);
                    break;
                case TurnTimelineRuntimeSignalKind.ParryWindowOpened:
                    _turnTimeline.MarkActiveParryOpen(signal.TimestampLocal, signal.FrameIndex);
                    break;
                case TurnTimelineRuntimeSignalKind.ParrySucceeded:
                    _turnTimeline.MarkActiveParried(signal.TimestampLocal, signal.FrameIndex);
                    break;
                case TurnTimelineRuntimeSignalKind.ParryMissed:
                    _turnTimeline.MarkActiveMissed(signal.Reason, signal.TimestampLocal, signal.FrameIndex);
                    break;
                case TurnTimelineRuntimeSignalKind.QueueFlushed:
                    _turnTimeline.AppendFlushMarker(signal.CueTurnId, signal.TimestampLocal, signal.FrameIndex);
                    break;
            }
        }

        flush_turn_timeline_events_to_log();
    }
}
