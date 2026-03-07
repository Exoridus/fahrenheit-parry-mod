using Fahrenheit.Mods.Parry;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class TurnTimelineTrackerTests {
    [Fact]
    public void Tracker_ShouldFollowParryLifecycleSequenceAcrossThreeQueuedTurns() {
        var tracker = new TurnTimelineTracker(128);
        tracker.BeginBattle();

        DateTime now = new(2026, 2, 28, 12, 0, 0, DateTimeKind.Local);
        ulong frame = 1000;
        int turnId = 111;

        var e1 = create_cue(queueIndex: 0, attackerId: 10, actor: "E1", action: "Attack", targets: "Yuna");
        var e2 = create_cue(queueIndex: 1, attackerId: 11, actor: "E2", action: "Attack", targets: "Yuna");
        var e3 = create_cue(queueIndex: 2, attackerId: 12, actor: "E3", action: "Attack", targets: "Yuna");

        // Step 1: E1 active, E2/E3 pending.
        tracker.UpdateCues([e1, e2, e3], turnId, now, frame++, parryWindowActive: false);
        assert_states(tracker, "E1", TurnTimelineParryState.Waiting, TurnTimelineLifecycleState.Active);
        assert_states(tracker, "E2", TurnTimelineParryState.Pending, TurnTimelineLifecycleState.Pending);
        assert_states(tracker, "E3", TurnTimelineParryState.Pending, TurnTimelineLifecycleState.Pending);

        // Step 2: E1 window opens.
        tracker.UpdateCues([e1, e2, e3], turnId, now.AddSeconds(1), frame++, parryWindowActive: true);
        assert_states(tracker, "E1", TurnTimelineParryState.Open, TurnTimelineLifecycleState.Active);

        // Step 3: E1 parried -> completed; E2 promoted.
        tracker.MarkActiveParried(now.AddSeconds(2), frame++);
        assert_states(tracker, "E1", TurnTimelineParryState.Parried, TurnTimelineLifecycleState.Completed);
        assert_states(tracker, "E2", TurnTimelineParryState.Waiting, TurnTimelineLifecycleState.Active);
        assert_states(tracker, "E3", TurnTimelineParryState.Pending, TurnTimelineLifecycleState.Pending);

        // Step 4: impact occurs outside active parry window -> E2 missed and completed; E3 promoted.
        tracker.MarkActiveMissed("impact outside active parry window", now.AddSeconds(3), frame++);
        assert_states(tracker, "E2", TurnTimelineParryState.Missed, TurnTimelineLifecycleState.Completed);
        assert_states(tracker, "E3", TurnTimelineParryState.Waiting, TurnTimelineLifecycleState.Active);

        // Step 5: E3 window opens.
        tracker.MarkActiveParryOpen(now.AddSeconds(4), frame++);
        assert_states(tracker, "E3", TurnTimelineParryState.Open, TurnTimelineLifecycleState.Active);

        // Step 6: E3 parried and completed.
        tracker.MarkActiveParried(now.AddSeconds(5), frame++);
        assert_states(tracker, "E3", TurnTimelineParryState.Parried, TurnTimelineLifecycleState.Completed);
    }

    [Fact]
    public void Tracker_ShouldUseStableFingerprintMatchingForDuplicateCues() {
        var tracker = new TurnTimelineTracker(128);
        tracker.BeginBattle();

        DateTime now = new(2026, 2, 28, 13, 0, 0, DateTimeKind.Local);
        ulong frame = 2000;
        int turnId = 222;

        // Two semantically identical cues (same fingerprint).
        var c1 = create_cue(queueIndex: 0, attackerId: 10, actor: "E1", action: "Attack", targets: "P1");
        var c2 = create_cue(queueIndex: 1, attackerId: 10, actor: "E1", action: "Attack", targets: "P1");
        tracker.UpdateCues([c1, c2], turnId, now, frame++, parryWindowActive: false);

        int firstRowId = find_first_row_id(tracker, "E1", ordinal: 1);
        int secondRowId = find_first_row_id(tracker, "E1", ordinal: 2);
        Assert.True(firstRowId > 0);
        Assert.True(secondRowId > 0);
        Assert.NotEqual(firstRowId, secondRowId);

        // Reduce to one cue: one row remains active and one is consumed/completed.
        tracker.UpdateCues([c1], turnId, now.AddSeconds(1), frame++, parryWindowActive: false);
        int activeCount = count_rows(tracker, r => !r.IsFlushMarker && r.Lifecycle == TurnTimelineLifecycleState.Active);
        int completedCount = count_rows(tracker, r => !r.IsFlushMarker && r.Lifecycle == TurnTimelineLifecycleState.Completed);
        Assert.Equal(1, activeCount);
        Assert.True(completedCount >= 1);

        // Back to two cues: active row is reused and exactly one new row is created.
        tracker.UpdateCues([c1, c2], turnId, now.AddSeconds(2), frame++, parryWindowActive: false);
        int distinctCueRows = count_rows(tracker, r => !r.IsFlushMarker && r.Actor == "E1");
        Assert.True(distinctCueRows >= 3);
    }

    [Fact]
    public void Tracker_ShouldNotForceUnknownParryabilityToMissedOnConsume() {
        var tracker = new TurnTimelineTracker(64);
        tracker.BeginBattle();

        DateTime now = new(2026, 2, 28, 14, 0, 0, DateTimeKind.Local);
        ulong frame = 3000;
        int turnId = 333;

        var unknown = create_cue(
            queueIndex: 0,
            attackerId: 12,
            actor: "E3",
            action: "Special",
            targets: "-",
            parryability: TurnTimelineParryability.Unknown);

        tracker.UpdateCues([unknown], turnId, now, frame++, parryWindowActive: false);
        tracker.UpdateCues([], turnId, now.AddSeconds(1), frame++, parryWindowActive: false);

        TurnTimelineRow row = find_latest_row(tracker, "E3");
        Assert.Equal(TurnTimelineLifecycleState.Completed, row.Lifecycle);
        Assert.NotEqual(TurnTimelineParryState.Missed, row.ParryState);
    }

    [Fact]
    public void Tracker_ShouldCorrelateDispatchSignalsToSpecificQueuedTurn() {
        var tracker = new TurnTimelineTracker(64);
        tracker.BeginBattle();

        DateTime now = new(2026, 2, 28, 15, 0, 0, DateTimeKind.Local);
        ulong frame = 4000;
        int turnId = 444;

        var first = create_cue(queueIndex: 0, attackerId: 10, actor: "E1", action: "Attack", targets: "Tidus");
        var second = create_cue(queueIndex: 1, attackerId: 11, actor: "E2", action: "Attack", targets: "Yuna");
        tracker.UpdateCues([first, second], turnId, now, frame++, parryWindowActive: false);

        tracker.CorrelateDispatchStarted(attackerId: 11, queueIndex: 1, timestampLocal: now.AddMilliseconds(16), frameIndex: frame++, parryWindowActive: false);
        TurnTimelineRow e2 = find_latest_row(tracker, "E2");
        TurnTimelineRow e1 = find_latest_row(tracker, "E1");
        Assert.Equal(TurnTimelineLifecycleState.Active, e2.Lifecycle);
        Assert.Equal(TurnTimelineLifecycleState.Pending, e1.Lifecycle);

        tracker.CorrelateDispatchConsumed(attackerId: 11, queueIndex: 1, timestampLocal: now.AddMilliseconds(32), frameIndex: frame++, reason: "resolved");
        e2 = find_latest_row(tracker, "E2");
        e1 = find_latest_row(tracker, "E1");
        Assert.Equal(TurnTimelineLifecycleState.Completed, e2.Lifecycle);
        Assert.Equal(TurnTimelineLifecycleState.Active, e1.Lifecycle);
    }

    [Fact]
    public void Tracker_ShouldKeepRowsStableWhenDistinctCommandSignaturesSwapQueueOrder() {
        var tracker = new TurnTimelineTracker(64);
        tracker.BeginBattle();

        DateTime now = new(2026, 2, 28, 16, 0, 0, DateTimeKind.Local);
        ulong frame = 5000;
        int turnId = 555;

        var c1 = create_cue(queueIndex: 0, attackerId: 10, actor: "E1", action: "Attack", targets: "Tidus", commandSignature: 0x11111111u);
        var c2 = create_cue(queueIndex: 1, attackerId: 10, actor: "E1", action: "Attack", targets: "Tidus", commandSignature: 0x22222222u);
        tracker.UpdateCues([c1, c2], turnId, now, frame++, parryWindowActive: false);
        int baselineCueRows = count_rows(tracker, r => !r.IsFlushMarker && !r.IsDiagnosticMarker);

        // Same two actions, but queue order flips.
        var c2First = create_cue(queueIndex: 0, attackerId: 10, actor: "E1", action: "Attack", targets: "Tidus", commandSignature: 0x22222222u);
        var c1Second = create_cue(queueIndex: 1, attackerId: 10, actor: "E1", action: "Attack", targets: "Tidus", commandSignature: 0x11111111u);
        tracker.UpdateCues([c2First, c1Second], turnId, now.AddSeconds(1), frame++, parryWindowActive: false);

        int activeRows = count_rows(tracker, r => !r.IsFlushMarker && r.Lifecycle != TurnTimelineLifecycleState.Completed);
        Assert.True(activeRows == 2, dump_rows(tracker));
        Assert.Equal(baselineCueRows, count_rows(tracker, r => !r.IsFlushMarker && !r.IsDiagnosticMarker));
    }

    [Fact]
    public void Tracker_ShouldCorrelateDamageResolvedToContextualRowWhenAttackerAndQueueAreProvided() {
        var tracker = new TurnTimelineTracker(64);
        tracker.BeginBattle();

        DateTime now = new(2026, 3, 7, 10, 0, 0, DateTimeKind.Local);
        ulong frame = 6000;
        int turnId = 666;

        var first = create_cue(queueIndex: 0, attackerId: 10, actor: "E1", action: "Attack", targets: "Tidus");
        var second = create_cue(queueIndex: 1, attackerId: 11, actor: "E2", action: "Spell", targets: "Yuna");
        tracker.UpdateCues([first, second], turnId, now, frame++, parryWindowActive: false);

        tracker.CorrelateDamageResolved(
            targetSlot: 1,
            timestampLocal: now.AddMilliseconds(16),
            frameIndex: frame++,
            attackerId: 11,
            queueIndex: 1,
            commandId: 0x4010,
            commandLabel: "Flame Ball",
            sourceStage: "hook_setdamage_pre");

        var events = new List<TurnTimelineEvent>();
        tracker.DrainEvents(events);
        TurnTimelineEvent damageEvent = events.Last(e => e.Kind == TurnTimelineEventKind.DamageResolved);
        TurnTimelineRow e2 = find_latest_row(tracker, "E2");

        Assert.Equal(e2.RowId, damageEvent.RowId);
        Assert.Contains("hook_setdamage_pre", damageEvent.Message, StringComparison.Ordinal);
        Assert.Contains("0x4010", damageEvent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tracker_ShouldFallbackDamageResolvedToActiveRowWithoutContext() {
        var tracker = new TurnTimelineTracker(64);
        tracker.BeginBattle();

        DateTime now = new(2026, 3, 7, 10, 15, 0, DateTimeKind.Local);
        ulong frame = 7000;
        int turnId = 777;

        var first = create_cue(queueIndex: 0, attackerId: 10, actor: "E1", action: "Attack", targets: "Tidus");
        var second = create_cue(queueIndex: 1, attackerId: 11, actor: "E2", action: "Attack", targets: "Yuna");
        tracker.UpdateCues([first, second], turnId, now, frame++, parryWindowActive: false);

        tracker.CorrelateDamageResolved(
            targetSlot: 0,
            timestampLocal: now.AddMilliseconds(16),
            frameIndex: frame++);

        var events = new List<TurnTimelineEvent>();
        tracker.DrainEvents(events);
        TurnTimelineEvent damageEvent = events.Last(e => e.Kind == TurnTimelineEventKind.DamageResolved);
        TurnTimelineRow e1 = find_latest_row(tracker, "E1");

        Assert.Equal(e1.RowId, damageEvent.RowId);
        Assert.Contains("impact", damageEvent.Message, StringComparison.Ordinal);
    }

    private static TurnTimelineCueObservation create_cue(
        int queueIndex,
        byte attackerId,
        string actor,
        string action,
        string targets,
        uint commandSignature = 0xA5A5A5A5u,
        TurnTimelineParryability parryability = TurnTimelineParryability.Parryable) {
        return new TurnTimelineCueObservation(
            QueueIndex: queueIndex,
            AttackerId: attackerId,
            Actor: actor,
            Action: action,
            Targets: targets,
            Parryability: parryability,
            Command: TurnTimelineCommandInfo.Empty,
            Fingerprint: new TurnTimelineCueFingerprint(
                AttackerId: attackerId,
                CommandCount: 1,
                CommandSignature: commandSignature,
                PartyMask: 0x1,
                NonPartyMask: 0,
                IsEnemy: true,
                IsMagic: false));
    }

    private static void assert_states(
        TurnTimelineTracker tracker,
        string actor,
        TurnTimelineParryState parry,
        TurnTimelineLifecycleState lifecycle) {
        TurnTimelineRow row = find_latest_row(tracker, actor);
        Assert.Equal(parry, row.ParryState);
        Assert.Equal(lifecycle, row.Lifecycle);
    }

    private static TurnTimelineRow find_latest_row(TurnTimelineTracker tracker, string actor) {
        for (int i = tracker.RowCount - 1; i >= 0; i--) {
            TurnTimelineRow row = tracker.GetRowAt(i);
            if (row.IsFlushMarker) continue;
            if (string.Equals(row.Actor, actor, StringComparison.Ordinal)) return row;
        }

        throw new InvalidOperationException($"No row found for actor '{actor}'.");
    }

    private static int find_first_row_id(TurnTimelineTracker tracker, string actor, int ordinal) {
        for (int i = 0; i < tracker.RowCount; i++) {
            TurnTimelineRow row = tracker.GetRowAt(i);
            if (row.IsFlushMarker) continue;
            if (!string.Equals(row.Actor, actor, StringComparison.Ordinal)) continue;
            if (row.TurnOrdinal != ordinal) continue;
            return row.RowId;
        }

        return -1;
    }

    private static int count_rows(TurnTimelineTracker tracker, Func<TurnTimelineRow, bool> predicate) {
        int count = 0;
        for (int i = 0; i < tracker.RowCount; i++) {
            TurnTimelineRow row = tracker.GetRowAt(i);
            if (predicate(row)) count++;
        }

        return count;
    }

    private static string dump_rows(TurnTimelineTracker tracker) {
        var lines = new List<string>();
        for (int i = 0; i < tracker.RowCount; i++) {
            TurnTimelineRow row = tracker.GetRowAt(i);
            lines.Add($"#{i}: id={row.RowId} actor={row.Actor} turn={row.TurnId}.{row.TurnOrdinal} q={row.QueuePosition}/{row.QueueTotal} life={row.Lifecycle} parry={row.ParryState} marker={row.IsFlushMarker}/{row.IsDiagnosticMarker}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
