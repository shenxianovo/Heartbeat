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
            pending.Stage);
        var observations = Channel.CreateUnbounded<SessionInput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var receiptGate = new object();
        long stateSequence = 0;
        void Observe(MacSystemObservation observation)
        {
            lock (receiptGate)
            {
                if (observation is not MacSystemObservation.Input) stateSequence++;
                observations.Writer.TryWrite(new SessionInput.Observation(observation, clock.GetUtcNow()));
            }
        }

        bool CaptureAndProject()
        {
            long before;
            while (true)
            {
                SessionInput? queued;
                lock (receiptGate)
                {
                    if (!observations.Reader.TryRead(out queued))
                    {
                        before = stateSequence;
                        break;
                    }
                }
                // Timer requests may have waited ahead of newer native events. Consume all
                // already-received events before taking a current snapshot; coalesce ticks.
                if (queued is SessionInput.Observation observation)
                    projector.Apply(observation.Value, observation.At);
            }
            source.RefreshCapabilities();
            var snapshot = source.Capture();
            DateTimeOffset at;
            lock (receiptGate)
            {
                // Native state changed while Capture was reading. Its queued event owns that
                // transition; a possibly older snapshot must not overwrite it.
                if (before != stateSequence) return false;
                at = clock.GetUtcNow();
            }
            projector.Confirm(snapshot, at);
            return true;
        }

        bool Process(SessionInput input)
        {
            if (input is SessionInput.Observation observation)
            {
                projector.Apply(observation.Value, observation.At);
                return false;
            }
            return CaptureAndProject();
        }

        source.Observation += Observe;
        try
        {
            source.StartObserving();
            observations.Writer.TryWrite(new SessionInput.Sample());
            while (observations.Reader.TryRead(out var initial))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Process(initial)) break;
                if (initial is SessionInput.Sample)
                    observations.Writer.TryWrite(new SessionInput.Sample());
            }

            if (options.Once)
            {
                await SubmitPendingAsync(cancellationToken);
                return;
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var processing = ProcessObservationsAsync(stop.Token);
            var sampling = SampleAsync(stop.Token);
            var submitting = SubmitAsync(stop.Token);
            await Task.WhenAll(processing, sampling, submitting);

            async Task ProcessObservationsAsync(CancellationToken token)
            {
                try
                {
                    await foreach (var observation in observations.Reader.ReadAllAsync(token))
                    {
                        _ = Process(observation);
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
                        observations.Writer.TryWrite(new SessionInput.Sample());
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
            source.Observation -= Observe;
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
