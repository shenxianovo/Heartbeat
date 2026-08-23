using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Serilog;

namespace Heartbeat.Collector.System.Collection;

/// <summary>
/// InProcess Transport Binding for the system Collector. Snapshots enter a Collector-owned
/// outbox before the adapter translates them into the same protocol messages used by reference
/// packages; only an ACK removes an entry.
/// </summary>
public sealed class SystemCollectorProtocolAdapter : ISystemSegmentPublisher
{
    private readonly object _gate = new();
    private readonly List<SystemCollectorOutboxEntry> _outbox = [];
    private string? _outboxPath;
    private DateTimeOffset? _retryNotBeforeUtc;
    private CancellationTokenSource? _retryCancellation;
    private FactStreamDescriptor? _descriptor;
    private InProcessFactStream? _stream;

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
            _outboxPath = fullPath;
            _outbox.AddRange(SystemCollectorOutbox.Load(_outboxPath));
        }
    }

    internal void BeginOpening(FactStreamDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            if (_descriptor is not null || _stream is not null)
                throw new InvalidOperationException("The system Collector foreground Stream is already opening.");
            _descriptor = descriptor;
            _retryNotBeforeUtc = null;
        }
    }

    internal void Open(InProcessFactStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (_gate)
        {
            if (_descriptor?.StreamId != stream.Descriptor.StreamId || _stream is not null)
                throw new InvalidOperationException("The system Collector foreground Stream does not match streams.opened.");
            _stream = stream;
            FlushLocked();
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            CancelScheduledRetry();
            _stream = null;
            _descriptor = null;
        }
    }

    public void Publish(ForegroundSegmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            var descriptor = _descriptor ?? throw new InvalidOperationException(
                "The system Collector cannot publish before its foreground Stream begins opening.");
            UpsertPending(new SystemCollectorOutboxEntry(
                Guid.CreateVersion7(),
                ToSubmission(descriptor, snapshot)));
            PersistOutbox();
            if (_stream is not null)
                FlushLocked();
        }
    }

    private void FlushLocked()
    {
        while (_stream is not null && _outbox.Count != 0)
        {
            if (_retryNotBeforeUtc > DateTimeOffset.UtcNow)
                return;
            var entry = _outbox[0];
            FactBatchAcknowledgement acknowledgement;
            try
            {
                acknowledgement = _stream.PublishAsync(entry.MessageId, [entry.Fact])
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
                DeadLetterAndRemove(entry, acknowledgement.MessageError!);
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
                PersistOutbox();
                ScheduleRetry(TimeSpan.FromMilliseconds(
                    outcome.RetryAfterMilliseconds ?? 1_000));
                return;
            }
            if (!outcome.IsAcknowledged)
            {
                DeadLetterAndRemove(
                    entry,
                    outcome.Error ?? new CollectorProtocolError(
                        "fact_delivery_failed",
                        $"Hub returned '{outcome.Status}' for a system foreground Segment.",
                        false));
                continue;
            }

            _outbox.RemoveAt(0);
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
            return;
        }
        if (_outbox[index].Fact.Revision < entry.Fact.Revision)
            _outbox[index] = entry;
    }

    private void PersistOutbox()
    {
        if (_outboxPath is not null)
            SystemCollectorOutbox.Save(_outboxPath, _outbox);
    }

    private void DeadLetterAndRemove(
        SystemCollectorOutboxEntry entry,
        CollectorProtocolError error)
    {
        if (_outboxPath is null)
            throw new CollectorActivationException(error);
        SystemCollectorOutbox.AppendDeadLetter(_outboxPath, entry, error);
        _outbox.RemoveAt(0);
        CancelScheduledRetry();
        PersistOutbox();
        Log.Error(
            "system Collector foreground Fact {FactId}/{Revision} 被永久拒绝：{Code} {Message}",
            entry.Fact.FactId,
            entry.Fact.Revision,
            error.Code,
            error.Message);
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
                Log.Warning(exception, "system Collector foreground Fact 后台重试失败，继续保留 outbox");
                lock (_gate)
                {
                    if (_stream is not null && _outbox.Count != 0)
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

}
