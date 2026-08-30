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
/// Maps system observations to domain-neutral Collector Facts. This adapter owns only the local
/// durable ingress handoff; Collector Protocol owns the outbox, remote ACK/retry, Gap and drain.
/// </summary>
public sealed class SystemCollectorProtocolAdapter :
    ISystemSegmentPublisher,
    ISystemInputEventPublisher,
    ICollectorClientDiagnostics
{
    public const string StatusStreamName = "system Collector 协议";
    private const int DefaultInputEventIngressCapacity = 100_000;
    private const int InputEventPumpBatchSize = 500;
    private const int SegmentPumpBatchSize = 500;

    private readonly Channel<ForegroundSegmentSnapshot> _segments = Channel.CreateUnbounded<ForegroundSegmentSnapshot>(
        new UnboundedChannelOptions
        {
            // Before Activation attachment, composition-time observations can only queue. Once
            // attached, callbacks append to the durable ingress journal before returning.
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Channel<InputEventItem> _inputEvents = Channel.CreateUnbounded<InputEventItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly SemaphoreSlim _pumpSignal = new(0, 1);
    private readonly UploadStatusRegistry? _statusRegistry;
    private SystemCollectorIngressStore? _ingressStore;
    private readonly int _inputEventIngressCapacity;
    private CollectorActivation? _activation;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pump;

    public SystemCollectorProtocolAdapter(
        UploadStatusRegistry? statusRegistry = null,
        int inputEventIngressCapacity = DefaultInputEventIngressCapacity)
    {
        if (inputEventIngressCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputEventIngressCapacity));
        _statusRegistry = statusRegistry;
        _inputEventIngressCapacity = inputEventIngressCapacity;
    }

    internal void Attach(CollectorActivation activation)
    {
        if (_activation is not null)
            throw new InvalidOperationException("The system Collector is already attached to an Activation.");
        _activation = activation;
        _ingressStore ??= SystemCollectorIngressStore.Open(Path.Combine(
            activation.Initialization.DataDirectory,
            "system-collector-ingress.json"),
            _inputEventIngressCapacity);
        PersistQueuedIngress();
        if (_ingressStore.PendingInputGapCount != 0)
            ReportPendingIngressGapStatus();
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

    internal async ValueTask PrepareDrainAsync(CancellationToken cancellationToken)
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
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Expected while draining.
            }
        }

        while (HasPendingIngress())
            await DrainOnceAsync(
                cancellationToken,
                segmentLimit: int.MaxValue,
                inputEventLimit: int.MaxValue).ConfigureAwait(false);
    }

    internal async ValueTask CompleteDrainAsync(CancellationToken cancellationToken)
    {
        if (_pumpCancellation is null)
            return;
        try
        {
            while (HasPendingIngress())
                await DrainOnceAsync(
                    cancellationToken,
                    segmentLimit: int.MaxValue,
                    inputEventLimit: int.MaxValue).ConfigureAwait(false);
        }
        finally
        {
            _pumpCancellation.Dispose();
            _pumpCancellation = null;
            _pump = null;
            _activation = null;
        }
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
            throw new InvalidOperationException("The system Collector InputEvent ingress is unavailable.");
        SignalPump();
    }

    public void Report(CollectorClientDiagnostic diagnostic)
    {
        if (_ingressStore?.PendingInputGapCount > 0)
        {
            ReportPendingIngressGapStatus();
            return;
        }
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

    private async Task DrainOnceAsync(
        CancellationToken cancellationToken,
        int segmentLimit = SegmentPumpBatchSize,
        int inputEventLimit = InputEventPumpBatchSize)
    {
        var activation = _activation;
        if (activation is null)
            return;
        PersistQueuedIngress();
        var ingress = _ingressStore
            ?? throw new InvalidOperationException("The system Collector durable ingress is unavailable.");

        var segments = ingress.PeekSegments(segmentLimit);
        if (segments.Count != 0)
        {
            await activation.PublishBatchAsync(
                segments.Select(item => ToFact(item.Snapshot)).ToArray(),
                cancellationToken).ConfigureAwait(false);
            ingress.AcknowledgeSegments(segments);
        }

        if (ingress.PeekInputGaps(1).FirstOrDefault() is { } dropped)
        {
            await activation.ReportGapAsync(new CollectorStreamGap(
                dropped.Gap.GapId,
                SystemInProcessCollector.InputEventBindingId,
                dropped.Gap.Start,
                dropped.Gap.End,
                "input_ingress_capacity_exceeded",
                dropped.Gap.EstimatedFactsLost), cancellationToken).ConfigureAwait(false);
            ingress.AcknowledgeInputGaps([dropped]);
            ReportReadyAfterIngressGapAcknowledged();
        }

        var inputEvents = ingress.PeekInputEvents(inputEventLimit);
        if (inputEvents.Count != 0)
        {
            await activation.PublishBatchAsync(
                inputEvents.Select(item => ToFact(item.Item)).ToArray(),
                cancellationToken).ConfigureAwait(false);
            ingress.AcknowledgeInputEvents(inputEvents);
        }
        if (HasPendingIngress())
            SignalPump();
    }

    private void PersistQueuedIngress()
    {
        var ingress = _ingressStore;
        if (ingress is null)
            return;
        while (_segments.Reader.TryRead(out var snapshot))
            ingress.Enqueue(snapshot);

        while (_inputEvents.Reader.TryRead(out var item))
        {
            if (ingress.StageInputEvent(item) == SystemInputIngressStageResult.GapStaged)
                ReportPendingIngressGapStatus();
        }
    }

    private bool HasPendingIngress()
    {
        if (_ingressStore?.HasPending == true ||
            _segments.Reader.TryPeek(out _) ||
            _inputEvents.Reader.TryPeek(out _))
            return true;
        return false;
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

    private void ReportPendingIngressGapStatus()
    {
        var count = _ingressStore?.PendingInputGapCount ?? 0;
        _statusRegistry?.Update(
            StatusStreamName,
            new UploadStreamStatus(
                UploadStreamState.GapRecorded,
                $"Durable InputEvent ingress Gap records {count} dropped event(s).",
                "Allow Collector Protocol delivery to report the retained Gap."));
    }

    private void ReportReadyAfterIngressGapAcknowledged()
    {
        if (_ingressStore?.PendingInputGapCount != 0)
        {
            ReportPendingIngressGapStatus();
            return;
        }
        _statusRegistry?.Update(StatusStreamName, UploadStreamStatus.Ready);
    }
}
