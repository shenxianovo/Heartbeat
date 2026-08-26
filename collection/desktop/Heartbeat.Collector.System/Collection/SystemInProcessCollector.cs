using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collector.System.Collection;

public sealed class SystemInProcessCollector(
    SystemCollectorProtocolAdapter protocol,
    AppMonitorService monitor) : IInProcessCollector
{
    public const string PackageId = "heartbeat.collector.system";
    public const string ForegroundBindingId = "foreground";
    public const string ForegroundOutputId = "foreground";
    public const string InputEventBindingId = "input-events";
    public const string InputEventOutputId = "input-events";

    private bool _monitorStarted;

    public string ArtifactId => "system.inprocess";

    public ProtocolSupport ProtocolSupport { get; } = new(
        [1],
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["facts.segment"] = [1],
            ["facts.event"] = [1],
            ["diagnostics.stream-gap"] = [1]
        });

    public void ConfigureOutbox(string path) => protocol.ConfigureOutbox(path);

    public ValueTask<InProcessCollectorInitialization> InitializeAsync(
        CollectorInitialization initialization,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (initialization.Instance.PackageId != PackageId)
            throw new InvalidOperationException(
                $"The system Collector cannot activate Package '{initialization.Instance.PackageId}'.");
        return ValueTask.FromResult(new InProcessCollectorInitialization(
            initialization.Spec.SpecRevision,
            [new OutputBinding(
                ForegroundBindingId,
                ForegroundOutputId,
                new Dictionary<string, string>(StringComparer.Ordinal)),
             new OutputBinding(
                 InputEventBindingId,
                 InputEventOutputId,
                 new Dictionary<string, string>(StringComparer.Ordinal))]));
    }

    public async ValueTask OnStreamsOpenedAsync(
        InProcessCollectorStreamsOpened opened,
        CancellationToken cancellationToken)
    {
        if (!opened.Streams.ContainsKey(ForegroundBindingId))
            throw new InvalidOperationException("The system Collector foreground Stream was not opened.");
        if (!opened.Streams.ContainsKey(InputEventBindingId))
            throw new InvalidOperationException("The system Collector Input Event Stream was not opened.");

        protocol.BeginOpening(opened.Streams);
        try
        {
            await monitor.StartAsync(cancellationToken);
            _monitorStarted = true;
            var activation = await opened.ReadyAsync(cancellationToken);
            protocol.Open(activation.Streams);
        }
        catch
        {
            if (_monitorStarted)
            {
                await monitor.StopAsync(CancellationToken.None);
                _monitorStarted = false;
            }
            await protocol.CloseAsync();
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_monitorStarted)
            {
                await monitor.StopAsync(cancellationToken);
                _monitorStarted = false;
            }
        }
        finally
        {
            await protocol.CloseAsync();
        }
    }
}
