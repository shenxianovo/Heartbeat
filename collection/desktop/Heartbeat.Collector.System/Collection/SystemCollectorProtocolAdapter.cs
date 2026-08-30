using System.Text.Json;
using System.Threading.Channels;
using Heartbeat.Collection.CollectorProtocol;
using Heartbeat.Collection.Hub.Collectors.Protocol;
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
    private const int IngressPersistenceBatchSize = 100;

    private readonly Channel<IReadOnlyList<ForegroundSegmentSnapshot>> _segments =
        Channel.CreateUnbounded<IReadOnlyList<ForegroundSegmentSnapshot>>(
        new UnboundedChannelOptions
        {
            // Platform callbacks always stop at this volatile queue. The background pump owns the
            // durable journal handoff so UI/window callbacks never synchronously wait for fsync.
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
    private readonly Action? _beforeIngressCommit;
    private ICollectorDurableCommitFence _ingressCommitFence;
    private SystemCollectorIngressStore? _ingressStore;
    private readonly int _inputEventIngressCapacity;
    private CollectorActivation? _activation;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pump;

    public SystemCollectorProtocolAdapter(
        UploadStatusRegistry? statusRegistry = null,
        int inputEventIngressCapacity = DefaultInputEventIngressCapacity)
        : this((Action?)null, statusRegistry, inputEventIngressCapacity)
    {
    }

    internal SystemCollectorProtocolAdapter(
        Action? beforeIngressCommit,
        UploadStatusRegistry? statusRegistry = null,
        int inputEventIngressCapacity = DefaultInputEventIngressCapacity)
    {
        if (inputEventIngressCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputEventIngressCapacity));
        _ingressCommitFence = new SystemCollectorIngressCommitFence();
        _beforeIngressCommit = beforeIngressCommit;
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
            _inputEventIngressCapacity,
            _ingressCommitFence,
            _beforeIngressCommit);
        PersistQueuedIngress();
        if (_ingressStore.PendingInputGapCount != 0)
            ReportPendingIngressGapStatus();
    }

    internal void AttachDurableIngressFence(ICollectorDurableCommitFence ingressCommitFence)
    {
        ArgumentNullException.ThrowIfNull(ingressCommitFence);
        if (_ingressStore is not null || _activation is not null)
            throw new InvalidOperationException(
                "The system Collector durable ingress fence must be attached before activation.");
        _ingressCommitFence = ingressCommitFence;
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
        PublishBatch([snapshot]);
    }

    public void PublishBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
            return;
        if (!_segments.Writer.TryWrite(snapshots.ToArray()))
            throw new InvalidOperationException("The system Collector segment ingress is unavailable.");
        SignalPump();
    }

    public void StageDurableBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
            return;
        var ingress = _ingressStore
            ?? throw new InvalidOperationException("The system Collector durable ingress is unavailable.");
        ingress.StageSegmentBatch(snapshots);
        SignalPump();
    }

    public void RecoverInterruptedSegment(DateTimeOffset recoveredAt)
    {
        var ingress = _ingressStore
            ?? throw new InvalidOperationException("The system Collector durable ingress is unavailable.");
        ingress.RecoverInterruptedSegment(recoveredAt);
        SignalPump();
    }

    public void ClearActiveCheckpoint(Guid factId, long revision)
    {
        var ingress = _ingressStore
            ?? throw new InvalidOperationException("The system Collector durable ingress is unavailable.");
        ingress.ClearActiveCheckpoint(factId, revision);
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

        var segmentBatches = ingress.PeekSegmentBatches(segmentLimit);
        if (segmentBatches.Count != 0)
        {
            await activation.PublishBatchAsync(
                segmentBatches.SelectMany(item => item.Snapshots).Select(ToFact).ToArray(),
                cancellationToken).ConfigureAwait(false);
            ingress.AcknowledgeSegmentBatches(segmentBatches);
        }

        if (ingress.PeekSegmentGaps(1).FirstOrDefault() is { } segmentGap)
        {
            await activation.ReportGapAsync(new CollectorStreamGap(
                segmentGap.Gap.GapId,
                SystemInProcessCollector.ForegroundBindingId,
                segmentGap.Gap.Start,
                segmentGap.Gap.End,
                segmentGap.Gap.Reason), cancellationToken).ConfigureAwait(false);
            ingress.AcknowledgeSegmentGaps([segmentGap]);
        }

        var inputDeliveries = ingress.PeekInputDeliveries(inputEventLimit);
        if (inputDeliveries.FirstOrDefault() is { Gap: { } inputGap } dropped)
        {
            await activation.ReportGapAsync(new CollectorStreamGap(
                inputGap.GapId,
                SystemInProcessCollector.InputEventBindingId,
                inputGap.Start,
                inputGap.End,
                "input_ingress_capacity_exceeded",
                inputGap.EstimatedFactsLost), cancellationToken).ConfigureAwait(false);
            ingress.AcknowledgeInputDeliveries([dropped]);
            ReportReadyAfterIngressGapAcknowledged();
        }
        else
        {
            var acceptedEvents = inputDeliveries
                .TakeWhile(delivery => delivery.Item is not null)
                .ToArray();
            if (acceptedEvents.Length != 0)
            {
                await activation.PublishBatchAsync(
                    acceptedEvents.Select(item => ToFact(item.Item!)).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                ingress.AcknowledgeInputDeliveries(acceptedEvents);
            }
        }
        if (HasPendingIngress())
            SignalPump();
    }

    private void PersistQueuedIngress()
    {
        var ingress = _ingressStore;
        if (ingress is null)
            return;
        while (_segments.Reader.TryRead(out var snapshots))
            ingress.StageSegmentBatch(snapshots);

        while (_inputEvents.Reader.TryPeek(out _))
        {
            var batch = new List<InputEventItem>(IngressPersistenceBatchSize);
            while (batch.Count < IngressPersistenceBatchSize && _inputEvents.Reader.TryRead(out var item))
                batch.Add(item);
            if (ingress.StageInputEvents(batch) != 0)
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
