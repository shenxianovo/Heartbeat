using System.Threading.Channels;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop;

public sealed class DesktopCollectorSession(
    string collectorKey,
    IDesktopObservationSource source,
    IHubSubmissionClient client,
    TimeProvider timeProvider)
{
    public async Task RunAsync(DesktopCollectionOptions options, CancellationToken cancellationToken)
    {
        var pending = new PendingHubSubmissions();
        var clock = new ContinuousObservationClock(timeProvider);
        var projector = new DesktopRecordProjector(
            collectorKey,
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
            queue.Start(cancellationToken);

            if (options.Once)
            {
                await SubmitPendingAsync(pending, options.Once, cancellationToken);
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
                        await SubmitPendingAsync(pending, options.Once, token);
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
            if (!options.Once && cancellationToken.IsCancellationRequested)
            {
                queue.Drain();
                using var finalSubmission = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SubmitPendingAsync(pending, options.Once, finalSubmission.Token, retry: false);
            }
        }

    }

    private async Task SubmitPendingAsync(PendingHubSubmissions pending, bool once, CancellationToken token, bool retry = true)
    {
        foreach (var batch in pending.ReadBatches())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await client.SubmitAsync(batch.ToSubmission(), token);
                pending.Confirm(batch);
            }
            catch (Exception exception) when (retry && !once && !token.IsCancellationRequested &&
                exception is HttpRequestException or IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Hub submission failed; keeping {batch.Records.Count} Record(s) for retry: {exception.Message}");
            }
        }
    }

    private abstract record SessionInput
    {
        public sealed record Observation(DesktopObservation Value, DateTimeOffset At) : SessionInput;
        public sealed record Sample : SessionInput;
    }

    /// <summary>
    /// 串行处理原生事件与周期采样。读取期间前台变化时，丢弃过时快照，由事件处理转场。
    /// </summary>
    private sealed class ObservationQueue(
        IDesktopObservationSource source,
        DesktopRecordProjector projector,
        ContinuousObservationClock clock)
    {
        private readonly Channel<SessionInput> _inputs = Channel.CreateUnbounded<SessionInput>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly object _gate = new();
        private long _activitySequence;

        public ChannelReader<SessionInput> Reader => _inputs.Reader;

        public void Start(CancellationToken cancellationToken)
        {
            RequestSample();
            while (Reader.TryRead(out var initial))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Process(initial)) break;
                if (initial is SessionInput.Sample) RequestSample();
            }
        }

        public void RequestSample() => _inputs.Writer.TryWrite(new SessionInput.Sample());

        public void Receive(DesktopObservation observation)
        {
            lock (_gate)
            {
                // Only a change of what is in the foreground can make a snapshot stale. Capability
                // and input events cannot, and capability events are published by Capture itself,
                // so counting them would let a snapshot invalidate its own confirmation.
                if (observation is DesktopObservation.Activity
                    or DesktopObservation.AwayEntered
                    or DesktopObservation.AwayExited)
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
        /// 应用所有已收到的事件。
        /// </summary>
        public void Drain() => DrainReceived();

        // 在同一次加锁中确认队列为空并读取活动序号，避免漏判快照读取期间到达的事件。
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
