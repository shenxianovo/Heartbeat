using System.Text.Json;
using System.Threading.Channels;
using Heartbeat.Collection.CollectorProtocol;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Input;
using Serilog;

namespace Heartbeat.Collector.System.Collection;

public interface ISystemInputEventPublisher
{
    void Publish(InputEventItem item);
}

/// <summary>
/// Maps system observations to domain-neutral Collector Facts. Lifecycle, persistent delivery,
/// ACK/retry, Gap and drain are owned by the Collector Protocol client module.
/// </summary>
public sealed class SystemCollectorProtocolAdapter :
    ISystemSegmentPublisher,
    ISystemInputEventPublisher,
    ICollectorClientDiagnostics
{
    public const string StatusStreamName = "system Collector 协议";
    private const int InputEventIngressCapacity = 100_000;
    private const int InputEventPumpBatchSize = 500;
    private const int SegmentPumpBatchSize = 500;

    private readonly Channel<ForegroundSegmentSnapshot> _segments = Channel.CreateUnbounded<ForegroundSegmentSnapshot>(
        new UnboundedChannelOptions
        {
            // Native desktop callbacks must only enqueue. Segment snapshots are low-rate,
            // ordered state revisions, so they are never dropped under protocol backpressure.
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Channel<InputEventItem> _inputEvents = Channel.CreateBounded<InputEventItem>(
        new BoundedChannelOptions(InputEventIngressCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly SemaphoreSlim _pumpSignal = new(0, 1);
    private readonly object _dropGate = new();
    private readonly UploadStatusRegistry? _statusRegistry;
    private CollectorActivation? _activation;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pump;
    private DateTimeOffset? _droppedStart;
    private DateTimeOffset? _droppedEnd;
    private int _droppedCount;
    private ForegroundSegmentSnapshot? _pendingSegment;

    public SystemCollectorProtocolAdapter(UploadStatusRegistry? statusRegistry = null)
    {
        _statusRegistry = statusRegistry;
    }

    internal void Attach(CollectorActivation activation)
    {
        if (_activation is not null)
            throw new InvalidOperationException("The system Collector is already attached to an Activation.");
        _activation = activation;
    }

    internal void Start()
    {
        if (_activation is null)
            throw new InvalidOperationException("The system Collector has no live Activation.");
        if (_pump is not null)
            throw new InvalidOperationException("The system Collector Input Event pump is already running.");
        _pumpCancellation = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_pumpCancellation.Token), CancellationToken.None);
        SignalPump();
    }

    internal async ValueTask StopAsync()
    {
        if (_pumpCancellation is null)
            return;

        // Stop the background reader before the final drain so publication remains ordered and
        // the monitor's terminal snapshot is durably handed to Collector Protocol before drain.
        SignalPump();
        await _pumpCancellation.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while draining.
            }
        }

        while (HasPendingIngress())
            await DrainOnceAsync(CancellationToken.None).ConfigureAwait(false);

        _pumpCancellation.Dispose();
        _pumpCancellation = null;
        _pump = null;
        _activation = null;
    }

    public void Publish(ForegroundSegmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_segments.Writer.TryWrite(snapshot))
            throw new InvalidOperationException("The system Collector segment ingress is unavailable.");
        SignalPump();
    }

    public void Publish(InputEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_inputEvents.Writer.TryWrite(item))
        {
            lock (_dropGate)
            {
                _droppedStart ??= item.Timestamp;
                _droppedEnd = item.Timestamp;
                _droppedCount++;
            }
        }
        SignalPump();
    }

    public void Report(CollectorClientDiagnostic diagnostic)
    {
        _statusRegistry?.Update(
            StatusStreamName,
            new UploadStreamStatus(
                UploadStreamState.Ready,
                diagnostic.Error,
                "查看诊断文件",
                diagnostic.DeadLetterCount,
                DeadLetterPath: diagnostic.DeadLetterPath));
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _pumpSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DrainOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error(exception, "system Collector Input Event 持久交付失败，将继续重试");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                SignalPump();
            }
        }
    }

    private async Task DrainOnceAsync(CancellationToken cancellationToken)
    {
        var activation = _activation;
        if (activation is null)
            return;

        var processedSegments = 0;
        while (processedSegments < SegmentPumpBatchSize && TryReadSegment(out var snapshot))
        {
            try
            {
                await activation.PublishAsync(ToFact(snapshot), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _pendingSegment = snapshot;
                throw;
            }
            processedSegments++;
        }

        DateTimeOffset? droppedStart;
        DateTimeOffset? droppedEnd;
        int droppedCount;
        lock (_dropGate)
        {
            droppedStart = _droppedStart;
            droppedEnd = _droppedEnd;
            droppedCount = _droppedCount;
            _droppedStart = null;
            _droppedEnd = null;
            _droppedCount = 0;
        }
        if (droppedStart is not null)
        {
            await activation.ReportGapAsync(new CollectorStreamGap(
                Guid.CreateVersion7(),
                SystemInProcessCollector.InputEventBindingId,
                droppedStart.Value,
                droppedEnd!.Value,
                "input_ingress_capacity_exceeded",
                droppedCount), cancellationToken).ConfigureAwait(false);
        }

        var processed = 0;
        while (processed < InputEventPumpBatchSize && _inputEvents.Reader.TryRead(out var item))
        {
            try
            {
                await activation.PublishAsync(ToFact(item), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _inputEvents.Writer.TryWrite(item);
                throw;
            }
            processed++;
        }
        if (HasPendingIngress())
            SignalPump();
    }

    private bool TryReadSegment(out ForegroundSegmentSnapshot snapshot)
    {
        if (_pendingSegment is not null)
        {
            snapshot = _pendingSegment;
            _pendingSegment = null;
            return true;
        }
        return _segments.Reader.TryRead(out snapshot!);
    }

    private bool HasPendingIngress()
    {
        if (_pendingSegment is not null ||
            _segments.Reader.TryPeek(out _) ||
            _inputEvents.Reader.TryPeek(out _))
            return true;
        lock (_dropGate)
            return _droppedStart is not null;
    }

    private static CollectorFact ToFact(ForegroundSegmentSnapshot snapshot) => new(
        SystemInProcessCollector.ForegroundBindingId,
        0,
        snapshot.FactId,
        snapshot.Revision,
        null,
        CollectorFactRecordState.Present,
        new CollectorSegmentFactTime(snapshot.Start, snapshot.End, snapshot.IsFinal),
        JsonSerializer.SerializeToElement(new
        {
            identityKey = snapshot.IdentityKey,
            appIdentityKey = snapshot.AppIdentityKey,
            appDisplayName = snapshot.AppDisplayName,
            title = snapshot.Title
        }));

    private static CollectorFact ToFact(InputEventItem item) => new(
        SystemInProcessCollector.InputEventBindingId,
        0,
        item.Id,
        1,
        null,
        CollectorFactRecordState.Present,
        new CollectorEventFactTime(item.Timestamp),
        JsonSerializer.SerializeToElement(new
        {
            eventType = EventTypeName(item.EventType),
            codeSet = item.CodeSet,
            code = item.Code
        }));

    private static string EventTypeName(InputEventType eventType) => eventType switch
    {
        InputEventType.KeyDown => "keyDown",
        InputEventType.MouseButton => "mouseButton",
        InputEventType.MouseScroll => "mouseScroll",
        _ => eventType.ToString()
    };

    private void SignalPump()
    {
        if (_pumpSignal.CurrentCount != 0)
            return;
        try
        {
            _pumpSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another producer already signalled the single reader.
        }
    }
}
