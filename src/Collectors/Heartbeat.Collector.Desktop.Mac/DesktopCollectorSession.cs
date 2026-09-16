using System.Threading.Channels;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac;

internal sealed class DesktopCollectorSession(
    IMacSystemObservationSource source,
    HubSubmissionClient client,
    TimeProvider timeProvider)
{
    public async Task RunAsync(CollectorOptions options, CancellationToken cancellationToken)
    {
        var pending = new PendingHubSubmissions();
        var clock = new ContinuousObservationClock(timeProvider);
        var projector = new DesktopRecordProjector(
            options.Target,
            options.DisplayName,
            options.MaximumConfirmationGap,
            options.WindowTitleDwell,
            pending.Stage);
        var queue = new ObservationQueue(source, projector, clock);

        source.Observation += queue.Receive;
        try
        {
            source.StartObserving();
            queue.RequestSample();
            while (queue.Reader.TryRead(out var initial))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (queue.Process(initial)) break;
                if (initial is SessionInput.Sample) queue.RequestSample();
            }

            if (options.Once)
            {
                await SubmitPendingAsync(cancellationToken);
                return;
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAll(
                ProcessObservationsAsync(stop.Token),
                SampleAsync(stop.Token),
                SubmitAsync(stop.Token));

            async Task ProcessObservationsAsync(CancellationToken token)
            {
                try
                {
                    await foreach (var input in queue.Reader.ReadAllAsync(token))
                    {
                        // A snapshot dropped in favour of a newer foreground event still owes this
                        // period a confirmation; retry now instead of waiting a whole interval.
                        if (!queue.Process(input) && input is SessionInput.Sample) queue.RequestSample();
                    }
                }
                finally
                {
                    await stop.CancelAsync();
                }
            }

            async Task SampleAsync(CancellationToken token)
            {
                try
                {
                    using var timer = new PeriodicTimer(options.Interval, timeProvider);
                    while (await timer.WaitForNextTickAsync(token))
                    {
                        queue.RequestSample();
                    }
                }
                finally
                {
                    await stop.CancelAsync();
                }
            }

            async Task SubmitAsync(CancellationToken token)
            {
                try
                {
                    while (true)
                    {
                        await SubmitPendingAsync(token);
                        await Task.Delay(options.Interval, timeProvider, token);
                    }
                }
                finally
                {
                    await stop.CancelAsync();
                }
            }
        }
        finally
        {
            source.Observation -= queue.Receive;
            source.StopObserving();
        }

        async Task SubmitPendingAsync(CancellationToken token)
        {
            foreach (var batch in pending.ReadBatches())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await client.SubmitAsync(batch.ToSubmission(), token);
                    pending.Confirm(batch);
                }
                catch (Exception exception) when (!options.Once && !token.IsCancellationRequested &&
                    exception is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
                {
                    Console.Error.WriteLine($"Hub submission failed; keeping {batch.Records.Count} Record(s) for retry: {exception.Message}");
                }
            }
        }
    }

    private abstract record SessionInput
    {
        public sealed record Observation(MacSystemObservation Value, DateTimeOffset At) : SessionInput;
        public sealed record Sample : SessionInput;
    }

    /// <summary>
    /// 把原生事件与周期采样收敛成一条串行的观测流。它只回答一件事：这次读数能不能算作确认。
    /// 读取快照期间前台变过，就让排队的那个事件拥有这次转场，可能已经过时的快照不覆盖它。
    /// </summary>
    private sealed class ObservationQueue(
        IMacSystemObservationSource source,
        DesktopRecordProjector projector,
        ContinuousObservationClock clock)
    {
        private readonly Channel<SessionInput> _inputs = Channel.CreateUnbounded<SessionInput>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly object _gate = new();
        private long _activitySequence;

        public ChannelReader<SessionInput> Reader => _inputs.Reader;

        public void RequestSample() => _inputs.Writer.TryWrite(new SessionInput.Sample());

        public void Receive(MacSystemObservation observation)
        {
            lock (_gate)
            {
                // Only a change of what is in the foreground can make a snapshot stale. Capability
                // and input events cannot, and capability events are published by Capture itself,
                // so counting them would let a snapshot invalidate its own confirmation.
                if (observation is MacSystemObservation.Activity
                    or MacSystemObservation.AwayEntered
                    or MacSystemObservation.AwayExited)
                {
                    _activitySequence++;
                }

                _inputs.Writer.TryWrite(new SessionInput.Observation(observation, clock.GetUtcNow()));
            }
        }

        /// <summary>处理一个输入，返回这次是否完成了一次确认。</summary>
        public bool Process(SessionInput input)
        {
            if (input is SessionInput.Observation observation)
            {
                projector.Apply(observation.Value, observation.At);
                return false;
            }
            return Confirm();
        }

        private bool Confirm()
        {
            var before = DrainReceived();
            source.RefreshCapabilities();
            var snapshot = source.Capture();
            // Capability events published while reading belong before this confirmation.
            DrainReceived();
            DateTimeOffset at;
            lock (_gate)
            {
                if (before != _activitySequence) return false;
                at = clock.GetUtcNow();
            }
            projector.Confirm(snapshot, at);
            return true;
        }

        /// <summary>
        /// 应用所有已收到的事件，返回队列见底那一刻的活动序号。序号与「队列已空」必须在同一次加锁里
        /// 取得，否则读快照期间到达的事件会既算进序号、又留在队列里，让确认与事件的顺序颠倒。
        /// </summary>
        private long DrainReceived()
        {
            while (true)
            {
                SessionInput? queued;
                lock (_gate)
                {
                    if (!_inputs.Reader.TryRead(out queued)) return _activitySequence;
                }
                // Timer requests may have waited ahead of newer native events. Consume all
                // already-received events before taking a current snapshot; coalesce ticks.
                if (queued is SessionInput.Observation observation)
                    projector.Apply(observation.Value, observation.At);
            }
        }
    }
}

internal sealed class ContinuousObservationClock
{
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _baseline;
    private readonly long _timestamp;

    public ContinuousObservationClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _baseline = timeProvider.GetUtcNow();
        _timestamp = timeProvider.GetTimestamp();
    }

    public DateTimeOffset GetUtcNow() =>
        _baseline + _timeProvider.GetElapsedTime(_timestamp, _timeProvider.GetTimestamp());
}
