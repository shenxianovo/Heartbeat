using System.Text.Json;
using System.Threading.Channels;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Input;
using Serilog;

namespace Heartbeat.Collector.System.Collection;

/// <summary>
/// InProcess Transport Binding for the system Collector. Snapshots enter a Collector-owned
/// outbox before the adapter translates them into the same protocol messages used by reference
/// packages; only an ACK or a durably recorded permanent rejection removes an entry.
/// </summary>
public interface ISystemInputEventPublisher
{
    void Publish(InputEventItem item);
}

public sealed class SystemCollectorProtocolAdapter : ISystemSegmentPublisher, ISystemInputEventPublisher
{
    public const string StatusStreamName = "system Collector 协议";
    private const int InputEventIngressCapacity = 100_000;
    private const int InputEventOutboxCapacity = 100_000;
    private const int InputEventPumpBatchSize = 500;

    private readonly object _gate = new();
    private readonly List<SystemCollectorOutboxEntry> _outbox = [];
    private readonly Channel<InputEventItem> _inputEvents = Channel.CreateBounded<InputEventItem>(
        new BoundedChannelOptions(InputEventIngressCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
    private readonly SemaphoreSlim _inputEventPumpSignal = new(0, 1);
    private readonly UploadStatusRegistry? _statusRegistry;
    private string? _outboxPath;
    private bool _outboxDirty;
    private int _deadLetterCount;
    private DateTimeOffset? _retryNotBeforeUtc;
    private CancellationTokenSource? _retryCancellation;
    private readonly Dictionary<string, FactStreamDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, InProcessFactStream> _streams = [];
    private CancellationTokenSource? _inputEventPumpCancellation;
    private Task? _inputEventPump;

    public SystemCollectorProtocolAdapter(UploadStatusRegistry? statusRegistry = null)
    {
        _statusRegistry = statusRegistry;
    }

    public void ConfigureOutbox(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            var fullPath = Path.GetFullPath(path);
            if (_outboxPath == fullPath)
                return;
            if (_outboxPath is not null)
                throw new InvalidOperationException("The system Collector outbox is already configured.");
            var restored = SystemCollectorOutbox.Load(fullPath);
            if (restored.Count(IsInputEvent) > InputEventOutboxCapacity)
                throw new InvalidDataException(
                    $"The system Collector outbox contains more than {InputEventOutboxCapacity} unacknowledged Input Events.");
            _outboxPath = fullPath;
            _outbox.AddRange(restored);
            _deadLetterCount = SystemCollectorOutbox.DeadLetterCount(_outboxPath);
            UpdateDeadLetterStatus(_deadLetterCount);
        }
    }

    internal void BeginOpening(IReadOnlyDictionary<string, FactStreamDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        lock (_gate)
        {
            if (_descriptors.Count != 0 || _streams.Count != 0)
                throw new InvalidOperationException("The system Collector Fact Streams are already opening.");
            foreach (var pair in descriptors)
                _descriptors.Add(pair.Key, pair.Value);
            _retryNotBeforeUtc = null;
        }
    }

    internal void Open(IReadOnlyDictionary<string, InProcessFactStream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        lock (_gate)
        {
            if (_streams.Count != 0 || streams.Count != _descriptors.Count ||
                streams.Any(pair => !_descriptors.TryGetValue(pair.Key, out var descriptor) ||
                                    descriptor.StreamId != pair.Value.Descriptor.StreamId))
                throw new InvalidOperationException("The system Collector Fact Streams do not match streams.opened.");
            foreach (var stream in streams.Values)
                _streams.Add(stream.Descriptor.StreamId, stream);
            FlushLocked();
            StartInputEventPumpLocked();
        }
    }

    internal async ValueTask CloseAsync()
    {
        CancellationTokenSource? cancellation;
        Task? pump;
        lock (_gate)
        {
            cancellation = _inputEventPumpCancellation;
            pump = _inputEventPump;
            _inputEventPumpCancellation = null;
            _inputEventPump = null;
        }
        cancellation?.Cancel();
        if (pump is not null)
        {
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // Expected while stopping the single background reader.
            }
        }
        cancellation?.Dispose();

        lock (_gate)
        {
            DrainInputEventsLocked();
            if (_outboxDirty)
                PersistOutbox();
            if (_streams.Count != 0)
                FlushLocked();
            CancelScheduledRetry();
            _streams.Clear();
            _descriptors.Clear();
        }
    }

    public void Publish(ForegroundSegmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            var descriptor = Descriptor(SystemInProcessCollector.ForegroundBindingId) ?? throw new InvalidOperationException(
                "The system Collector cannot publish before its foreground Stream begins opening.");
            UpsertPending(new SystemCollectorOutboxEntry(
                Guid.CreateVersion7(),
                ToSubmission(descriptor, snapshot)));
            PersistOutbox();
            if (_streams.Count != 0)
                FlushLocked();
        }
    }

    public void Publish(InputEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _inputEvents.Writer.TryWrite(item);
        SignalInputEventPump();
    }

    private void StartInputEventPumpLocked()
    {
        if (_inputEventPump is not null)
            throw new InvalidOperationException("The system Collector Input Event pump is already running.");
        var cancellation = new CancellationTokenSource();
        _inputEventPumpCancellation = cancellation;
        _inputEventPump = Task.Run(() => PumpInputEventsAsync(cancellation.Token));
    }

    private async Task PumpInputEventsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            bool shouldWait;
            lock (_gate)
                shouldWait = !_outboxDirty && !_inputEvents.Reader.TryPeek(out _);
            if (shouldWait)
                await _inputEventPumpSignal.WaitAsync(cancellationToken);

            var batch = new List<InputEventItem>(InputEventPumpBatchSize);
            var outboxFull = false;
            var persistenceFailed = false;
            try
            {
                lock (_gate)
                {
                    if (_outboxDirty)
                    {
                        PersistOutbox();
                        if (_streams.Count != 0)
                            FlushLocked();
                    }
                    var available = InputEventOutboxCapacity - _outbox.Count(IsInputEvent);
                    outboxFull = available <= 0;
                    var readLimit = Math.Clamp(available, 0, InputEventPumpBatchSize);
                    while (batch.Count < readLimit &&
                           _inputEvents.Reader.TryRead(out var item))
                        batch.Add(item);
                    if (batch.Count != 0)
                    {
                        EnqueueInputEventsLocked(batch);
                        PersistOutbox();
                        if (_streams.Count != 0)
                            FlushLocked();
                    }
                }
            }
            catch (Exception exception)
            {
                persistenceFailed = true;
                Log.Error(exception, "system Collector Input Event 后台持久化失败，将继续重试");
            }
            if (persistenceFailed || outboxFull)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private void SignalInputEventPump()
    {
        if (_inputEventPumpSignal.CurrentCount != 0)
            return;
        try
        {
            _inputEventPumpSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another producer won the race to signal the single background reader.
        }
    }

    private void MarkOutboxDirty()
    {
        _outboxDirty = true;
        SignalInputEventPump();
    }

    private void DrainInputEventsLocked()
    {
        var available = InputEventOutboxCapacity - _outbox.Count(IsInputEvent);
        var batch = new List<InputEventItem>(Math.Min(InputEventPumpBatchSize, Math.Max(available, 0)));
        while (batch.Count < available && _inputEvents.Reader.TryRead(out var item))
            batch.Add(item);
        if (batch.Count != 0)
        {
            EnqueueInputEventsLocked(batch);
            PersistOutbox();
        }
    }

    private void EnqueueInputEventsLocked(IReadOnlyList<InputEventItem> items)
    {
        var descriptor = Descriptor(SystemInProcessCollector.InputEventBindingId) ?? throw new InvalidOperationException(
            "The system Collector cannot persist Input Events before its Stream begins opening.");
        var pendingEventCount = _outbox.Count(IsInputEvent);
        if (items.Count > InputEventOutboxCapacity - pendingEventCount)
            throw new InvalidOperationException("The system Collector Input Event outbox is full.");
        foreach (var item in items)
        {
            _outbox.Add(new SystemCollectorOutboxEntry(
                Guid.CreateVersion7(),
                ToSubmission(descriptor, item)));
        }
        if (items.Count != 0)
            MarkOutboxDirty();
    }

    private static bool IsInputEvent(SystemCollectorOutboxEntry entry) =>
        entry.Fact.Time.OccurredAt is not null;

    private void FlushLocked()
    {
        while (_streams.Count != 0 && _outbox.Count != 0)
        {
            if (_retryNotBeforeUtc > DateTimeOffset.UtcNow)
                return;
            var entry = _outbox[0];
            if (!_streams.TryGetValue(entry.Fact.StreamId, out var stream))
                return;
            FactBatchAcknowledgement acknowledgement;
            try
            {
                acknowledgement = stream.PublishAsync(entry.MessageId, [entry.Fact])
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                // The attempt outcome is unknown. Retain both the Fact and messageId so the next
                // flush replays the same attempt exactly as Collector Protocol requires.
                Log.Warning(exception, "system Collector foreground Fact 发布结果未知，保留 outbox 等待重试");
                ScheduleRetry(TimeSpan.FromSeconds(1));
                return;
            }
            if (acknowledgement.IsMessageRejected)
            {
                if (!DeadLetterAndRemove(entry, acknowledgement.MessageError!))
                    return;
                continue;
            }

            if (acknowledgement.Results.Count != 1)
            {
                Log.Warning("system Collector foreground Fact 收到无效 ACK，保留原 messageId 等待重试");
                ScheduleRetry(TimeSpan.FromSeconds(1));
                return;
            }
            var outcome = acknowledgement.Results[0];
            if (outcome.Status == FactDeliveryStatus.Retry)
            {
                _outbox[0] = entry with { MessageId = Guid.CreateVersion7() };
                MarkOutboxDirty();
                PersistOutbox();
                ScheduleRetry(TimeSpan.FromMilliseconds(
                    outcome.RetryAfterMilliseconds ?? 1_000));
                return;
            }
            if (!outcome.IsAcknowledged)
            {
                if (!DeadLetterAndRemove(
                    entry,
                    outcome.Error ?? new CollectorProtocolError(
                        "fact_delivery_failed",
                        $"Hub returned '{outcome.Status}' for a system Collector Fact.",
                        false)))
                    return;
                continue;
            }

            _outbox.RemoveAt(0);
            MarkOutboxDirty();
            CancelScheduledRetry();
            PersistOutbox();
        }
    }

    private void UpsertPending(SystemCollectorOutboxEntry entry)
    {
        var index = _outbox.FindIndex(item => item.Fact.FactId == entry.Fact.FactId);
        if (index < 0)
        {
            _outbox.Add(entry);
            MarkOutboxDirty();
            return;
        }
        if (_outbox[index].Fact.Revision < entry.Fact.Revision)
        {
            _outbox[index] = entry;
            MarkOutboxDirty();
        }
    }

    private void PersistOutbox()
    {
        if (_outboxPath is not null)
            SystemCollectorOutbox.Save(_outboxPath, _outbox);
        _outboxDirty = false;
    }

    private bool DeadLetterAndRemove(
        SystemCollectorOutboxEntry entry,
        CollectorProtocolError error)
    {
        if (_outboxPath is null)
            throw new CollectorActivationException(error);
        int deadLetterCount;
        try
        {
            deadLetterCount = SystemCollectorOutbox.AppendDeadLetter(_outboxPath, entry, error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateDeadLetterStatus(
                _deadLetterCount,
                UploadStreamState.DeadLetterWriteFailed,
                "system Collector 永久拒绝记录无法写入 dead letter，协议 outbox 已保留该记录。");
            Log.Error(exception, "system Collector dead letter 写入失败，保留协议 outbox 记录");
            ScheduleRetry(TimeSpan.FromSeconds(1));
            return false;
        }
        _deadLetterCount = deadLetterCount;
        _outbox.RemoveAt(0);
        MarkOutboxDirty();
        CancelScheduledRetry();
        PersistOutbox();
        UpdateDeadLetterStatus(deadLetterCount);
        Log.Error(
            "system Collector Fact {FactId}/{Revision} 被永久拒绝：{Code} {Message}",
            entry.Fact.FactId,
            entry.Fact.Revision,
            error.Code,
            error.Message);
        return true;
    }

    private void UpdateDeadLetterStatus(
        int count,
        UploadStreamState state = UploadStreamState.Ready,
        string? message = null)
    {
        if (_statusRegistry is null || _outboxPath is null)
            return;
        _statusRegistry.Update(
            StatusStreamName,
            new UploadStreamStatus(
                state,
                message,
                "查看诊断文件",
                count,
                DeadLetterPath: SystemCollectorOutbox.DeadLetterPath(_outboxPath)));
    }

    private void ScheduleRetry(TimeSpan delay)
    {
        CancelScheduledRetry();
        _retryNotBeforeUtc = DateTimeOffset.UtcNow + delay;
        var cancellation = new CancellationTokenSource();
        _retryCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cancellation.Token);
                lock (_gate)
                {
                    if (!ReferenceEquals(_retryCancellation, cancellation))
                        return;
                    _retryCancellation = null;
                    _retryNotBeforeUtc = null;
                    cancellation.Dispose();
                    FlushLocked();
                }
            }
            catch (OperationCanceledException)
            {
                // Stream closed or a newer retry schedule replaced this attempt.
                cancellation.Dispose();
            }
            catch (Exception exception)
            {
                cancellation.Dispose();
                Log.Warning(exception, "system Collector Fact 后台重试失败，继续保留 outbox");
                lock (_gate)
                {
                    if (_streams.Count != 0 && _outbox.Count != 0)
                        ScheduleRetry(TimeSpan.FromSeconds(1));
                }
            }
        });
    }

    private void CancelScheduledRetry()
    {
        _retryNotBeforeUtc = null;
        _retryCancellation?.Cancel();
        _retryCancellation = null;
    }

    private static FactSubmission ToSubmission(
        FactStreamDescriptor descriptor,
        ForegroundSegmentSnapshot snapshot)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            identityKey = snapshot.IdentityKey,
            appIdentityKey = snapshot.AppIdentityKey,
            appDisplayName = snapshot.AppDisplayName,
            title = snapshot.Title
        });
        return new FactSubmission(
            descriptor.StreamId,
            descriptor.Schema.Revision,
            snapshot.FactId,
            snapshot.Revision,
            ObservedAt: null,
            FactRecordState.Present,
            new SegmentFactTime(snapshot.Start, snapshot.End, snapshot.IsFinal),
            payload);
    }

    private static FactSubmission ToSubmission(
        FactStreamDescriptor descriptor,
        InputEventItem item)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            eventType = EventTypeName(item.EventType),
            codeSet = item.CodeSet,
            code = item.Code
        });
        return new FactSubmission(
            descriptor.StreamId,
            descriptor.Schema.Revision,
            item.Id,
            Revision: 1,
            ObservedAt: null,
            FactRecordState.Present,
            new EventFactTime(item.Timestamp),
            payload);
    }

    private static string EventTypeName(InputEventType eventType) => eventType switch
    {
        InputEventType.KeyDown => "keyDown",
        InputEventType.MouseButton => "mouseButton",
        InputEventType.MouseScroll => "mouseScroll",
        _ => eventType.ToString()
    };

    private FactStreamDescriptor? Descriptor(string bindingId) =>
        _descriptors.GetValueOrDefault(bindingId);

}
