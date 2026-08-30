using System.Text.Json;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Core.DTOs.Input;

return args switch
{
    ["protocol-crash", var statePath, var segmentId, var inputId, var rejectedInputId] =>
        await CrashDuringProtocolReplayAsync(
            statePath,
            Guid.Parse(segmentId),
            Guid.Parse(inputId),
            Guid.Parse(rejectedInputId)),
    ["protocol-drain", var statePath, var instanceId, var segmentId, var inputId] =>
        await RestartAndDrainAsync(
            statePath,
            Guid.Parse(instanceId),
            Guid.Parse(segmentId),
            Guid.Parse(inputId)),
    ["protocol-verify", var statePath, var instanceId, var segmentId, var inputId] =>
        await RestartAndVerifyAsync(
            statePath,
            Guid.Parse(instanceId),
            Guid.Parse(segmentId),
            Guid.Parse(inputId)),
    _ => Usage()
};

static async Task<int> CrashDuringProtocolReplayAsync(
    string statePath,
    Guid segmentId,
    Guid inputId,
    Guid rejectedInputId)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
    var clock = new SystemClock();
    var segmentSink = new SegmentIngestService(clock);
    var inputSink = new CrashBlockingInputSink();
    var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
    using var config = JsonDocument.Parse("{}");
    var runtime = CollectorRuntime.Open(statePath, segmentSink, inputEventSink: inputSink);
    var instance = runtime.CreateInstance(
        package,
        new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
        new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
    var ingress = SystemCollectorIngressStore.Open(
        IngressPath(statePath, instance.CollectorInstanceId),
        inputCapacity: 1);
    ingress.StageSegmentBatch([Segment(segmentId)]);
    AssertStage(
        SystemInputIngressStageResult.EventStaged,
        ingress.StageInputEvent(Input(inputId, DateTimeOffset.UnixEpoch.AddMinutes(2))));
    AssertStage(
        SystemInputIngressStageResult.GapStaged,
        ingress.StageInputEvent(Input(rejectedInputId, DateTimeOffset.UnixEpoch.AddMinutes(3))));

    var protocol = new SystemCollectorProtocolAdapter(inputEventIngressCapacity: 1);
    _ = await runtime.ActivateInProcessAsync(
        instance.CollectorInstanceId,
        package,
        Collector(protocol, clock, segmentSink));
    if (!inputSink.Entered.Wait(TimeSpan.FromSeconds(5)))
        throw new TimeoutException("Real Collector Protocol delivery did not reach the blocking projection sink.");

    Console.WriteLine($"protocol-blocked-before-crash:{instance.CollectorInstanceId}");
    Console.Out.Flush();
    Environment.FailFast("Intentional crash while a real Collector Protocol delivery is in flight.");
    return 70;
}

static async Task<int> RestartAndDrainAsync(
    string statePath,
    Guid instanceId,
    Guid segmentId,
    Guid inputId)
{
    var clock = new SystemClock();
    var segmentSink = new SegmentIngestService(clock);
    var inputSink = new CapturingInputSink();
    var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
    await using var runtime = CollectorRuntime.Open(statePath, segmentSink, inputEventSink: inputSink);
    var instance = runtime.GetInstance(instanceId);
    if (instance.PackageId != SystemInProcessCollector.PackageId)
        throw new InvalidDataException("Restart restored the wrong Collector Instance.");
    var protocol = new SystemCollectorProtocolAdapter(inputEventIngressCapacity: 1);
    await using var activation = await runtime.ActivateInProcessAsync(
        instanceId,
        package,
        Collector(protocol, clock, segmentSink));

    await WaitUntilAsync(() =>
        inputSink.Ids.Contains(inputId)
        && RuntimeHasFact(statePath, segmentId)
        && RuntimeHasGap(statePath, "input_ingress_capacity_exceeded")
        && !SystemCollectorIngressStore.Open(IngressPath(statePath, instanceId), 1).HasPending
        && OutboxPendingCount(OutboxPath(statePath, instanceId)) == 0);
    await activation.StopAsync();
    var drain = activation.DrainResult
        ?? throw new InvalidDataException("The real InProcess Activation did not persist a drain result.");
    if (!drain.IsFullyDrained ||
        drain.LogicalResult.Reason != CollectorDrainReason.Drained ||
        drain.PendingFacts != 0 ||
        drain.PendingGaps != 0 ||
        !drain.LogicalResult.RemainderDurable ||
        drain.CompletionReason != CollectorDrainCompletionReason.Completed)
        throw new InvalidDataException($"Drain result was not truthful: {JsonSerializer.Serialize(drain)}");

    Console.WriteLine(
        $"protocol-drained:{drain.LogicalResult.Reason}:{drain.PendingFacts}:{drain.PendingGaps}:" +
        $"{drain.LogicalResult.RemainderDurable}:{drain.CompletionReason}");
    return 0;
}

static async Task<int> RestartAndVerifyAsync(
    string statePath,
    Guid instanceId,
    Guid segmentId,
    Guid inputId)
{
    var clock = new SystemClock();
    var segmentSink = new SegmentIngestService(clock);
    var inputSink = new CapturingInputSink();
    var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
    await using var runtime = CollectorRuntime.Open(statePath, segmentSink, inputEventSink: inputSink);
    if (!RuntimeHasFact(statePath, segmentId) ||
        !inputSink.Ids.Contains(inputId) ||
        !RuntimeHasGap(statePath, "input_ingress_capacity_exceeded"))
        throw new InvalidDataException("Committed Fact/Gap truth did not survive the second process restart.");
    if (SystemCollectorIngressStore.Open(IngressPath(statePath, instanceId), 1).HasPending ||
        OutboxPendingCount(OutboxPath(statePath, instanceId)) != 0)
        throw new InvalidDataException("Acknowledged protocol remainder resurrected after restart.");

    var protocol = new SystemCollectorProtocolAdapter(inputEventIngressCapacity: 1);
    await using var activation = await runtime.ActivateInProcessAsync(
        instanceId,
        package,
        Collector(protocol, clock, segmentSink));
    await activation.StopAsync();
    if (activation.DrainResult is not { IsFullyDrained: true, PendingFacts: 0, PendingGaps: 0 })
        throw new InvalidDataException("A clean restart did not remain fully drained.");

    Console.WriteLine("protocol-restart-no-remainder");
    return 0;
}

static SystemInProcessCollector Collector(
    SystemCollectorProtocolAdapter protocol,
    IClock clock,
    SegmentIngestService segmentSink) => new(
    protocol,
    new AppMonitorService(
        clock,
        new EmptyObservations(),
        new NoInputActivity(),
        protocol,
        segmentSink,
        new EmptySettings()));

static ForegroundSegmentSnapshot Segment(Guid id) => new(
    id,
    1,
    "system|cross-process",
    "win:cross-process",
    "Cross Process",
    "Smoke",
    DateTimeOffset.UnixEpoch,
    DateTimeOffset.UnixEpoch.AddMinutes(1),
    true);

static InputEventItem Input(Guid id, DateTimeOffset timestamp) => new()
{
    Id = id,
    EventType = InputEventType.MouseButton,
    CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
    Code = 1,
    Timestamp = timestamp
};

static string IngressPath(string statePath, Guid instanceId) => Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(statePath))!,
    "collector-data",
    instanceId.ToString("N"),
    "system-collector-ingress.json");

static string OutboxPath(string statePath, Guid instanceId) => Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(statePath))!,
    "collector-data",
    instanceId.ToString("N"),
    "collector-protocol-outbox.json");

static int OutboxPendingCount(string path)
{
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var state = document.RootElement.GetProperty("State");
        return state.GetProperty("Facts").GetArrayLength() + state.GetProperty("Gaps").GetArrayLength();
    }
    catch (Exception exception) when (exception is IOException or JsonException)
    {
        return -1;
    }
}

static bool RuntimeHasFact(string statePath, Guid factId)
{
    using var state = JsonDocument.Parse(File.ReadAllText(statePath));
    return state.RootElement.GetProperty("facts").EnumerateArray().Any(fact =>
        fact.GetProperty("factId").GetGuid() == factId);
}

static bool RuntimeHasGap(string statePath, string reason)
{
    using var state = JsonDocument.Parse(File.ReadAllText(statePath));
    return state.RootElement.GetProperty("gaps").EnumerateArray().Any(gap =>
        gap.GetProperty("reason").GetString() == reason);
}

static async Task WaitUntilAsync(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!condition())
        await Task.Delay(10, timeout.Token);
}

static void AssertStage(SystemInputIngressStageResult expected, SystemInputIngressStageResult actual)
{
    if (actual != expected)
        throw new InvalidDataException($"Expected ingress result {expected}, got {actual}.");
}

static int Usage()
{
    Console.Error.WriteLine(
        "Usage: protocol-crash <statePath> <segmentId> <inputId> <rejectedInputId> | " +
        "protocol-drain <statePath> <instanceId> <segmentId> <inputId> | " +
        "protocol-verify <statePath> <instanceId> <segmentId> <inputId>");
    return 64;
}

file sealed class CrashBlockingInputSink : IInputEventFactSink
{
    public ManualResetEventSlim Entered { get; } = new();

    public bool TryAccept(
        InputEventItem item,
        bool isReplay,
        ICollectorProjectionCommitFence commitFence)
    {
        Entered.Set();
        Thread.Sleep(Timeout.Infinite);
        return false;
    }
}

file sealed class CapturingInputSink : IInputEventFactSink
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _ids = [];

    public IReadOnlySet<Guid> Ids
    {
        get
        {
            lock (_gate)
                return _ids.ToHashSet();
        }
    }

    public bool TryAccept(
        InputEventItem item,
        bool isReplay,
        ICollectorProjectionCommitFence commitFence) => commitFence.TryCommit(() =>
        {
            lock (_gate)
                _ids.Add(item.Id);
        });
}

file sealed class EmptyObservations : IDesktopObservationSource
{
    public event Action<DesktopObservation>? Observation
    {
        add { }
        remove { }
    }
    public DesktopActivity CurrentActivity => DesktopActivity.None;
    public void Start() { }
    public void Stop() { }
}

file sealed class NoInputActivity : IInputActivitySignal
{
    public void MarkClick() { }
    public bool ClickedWithin(TimeSpan window) => false;
}

file sealed class EmptySettings : IDesktopSettings
{
    public IReadOnlyList<string> AwayProcessNames => [];
    public bool SplitFocusedWindowChangesUnconditionally => true;
    public event Action<IReadOnlyList<string>>? AwayProcessNamesChanged
    {
        add { }
        remove { }
    }
}
