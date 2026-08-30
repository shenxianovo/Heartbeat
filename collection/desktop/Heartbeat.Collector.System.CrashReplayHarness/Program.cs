using Heartbeat.Collector.System.Collection;
using Heartbeat.Core.DTOs.Input;

return args switch
{
    ["stage-crash", var path, var segmentId, var inputId, var rejectedInputId] =>
        StageAndCrash(path, Guid.Parse(segmentId), Guid.Parse(inputId), Guid.Parse(rejectedInputId)),
    ["replay-ack", var path, var segmentId, var inputId] =>
        ReplayAndAcknowledge(path, Guid.Parse(segmentId), Guid.Parse(inputId)),
    ["verify-empty", var path] => VerifyEmpty(path),
    _ => Usage()
};

static int StageAndCrash(string path, Guid segmentId, Guid inputId, Guid rejectedInputId)
{
    var store = SystemCollectorIngressStore.Open(path, inputCapacity: 1);
    store.StageSegmentBatch(
    [
        new ForegroundSegmentSnapshot(
            segmentId,
            1,
            "system|cross-process",
            "win:cross-process",
            "Cross Process",
            "Smoke",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            true)
    ]);
    if (store.StageInputEvent(Input(inputId, DateTimeOffset.UnixEpoch.AddMinutes(2))) !=
        SystemInputIngressStageResult.EventStaged)
        throw new InvalidOperationException("Expected the first InputEvent to enter durable ingress.");
    if (store.StageInputEvent(Input(rejectedInputId, DateTimeOffset.UnixEpoch.AddMinutes(3))) !=
        SystemInputIngressStageResult.GapStaged)
        throw new InvalidOperationException("Expected overflow to enter durable ingress as a Gap.");

    Console.WriteLine("staged-before-crash");
    Console.Out.Flush();
    Environment.FailFast("Intentional cross-process durability smoke crash.");
    return 70;
}

static int ReplayAndAcknowledge(string path, Guid segmentId, Guid inputId)
{
    var store = SystemCollectorIngressStore.Open(path, inputCapacity: 1);
    var segmentBatches = store.PeekSegmentBatches(10);
    var inputEvents = store.PeekInputEvents(10);
    var inputGaps = store.PeekInputGaps(10);
    var replayedSegment = AssertSingle(segmentBatches).Snapshots.Single();
    var replayedInput = AssertSingle(inputEvents).Item;
    var replayedGap = AssertSingle(inputGaps).Gap;
    if (replayedSegment.FactId != segmentId || replayedInput.Id != inputId ||
        replayedGap.EstimatedFactsLost != 1)
        throw new InvalidDataException("Restart did not replay the expected durable identities.");

    Console.WriteLine($"replayed:{segmentId}:{inputId}:{replayedGap.EstimatedFactsLost}");
    store.AcknowledgeSegmentBatches(segmentBatches);
    store.AcknowledgeInputEvents(inputEvents);
    store.AcknowledgeInputGaps(inputGaps);
    return 0;
}

static int VerifyEmpty(string path)
{
    var store = SystemCollectorIngressStore.Open(path, inputCapacity: 1);
    if (store.HasPending || store.ActiveSegmentCheckpoint is not null)
        throw new InvalidDataException("Acknowledged durable remainder reappeared after a second restart.");
    Console.WriteLine("durable-remainder-empty");
    return 0;
}

static T AssertSingle<T>(IReadOnlyList<T> items) => items.Count == 1
    ? items[0]
    : throw new InvalidDataException($"Expected one durable {typeof(T).Name}, found {items.Count}.");

static InputEventItem Input(Guid id, DateTimeOffset timestamp) => new()
{
    Id = id,
    EventType = InputEventType.MouseButton,
    CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
    Code = 1,
    Timestamp = timestamp
};

static int Usage()
{
    Console.Error.WriteLine(
        "Usage: stage-crash <path> <segmentId> <inputId> <rejectedInputId> | " +
        "replay-ack <path> <segmentId> <inputId> | verify-empty <path>");
    return 64;
}
