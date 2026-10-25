using System.Diagnostics;
using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Observers.ForegroundState;

internal sealed class ForegroundCollector(
    EntityId observerId,
    LocalApplicationRegistry applications,
    ForegroundStore store,
    TimeSpan interval,
    Func<ForegroundApplicationReading> read,
    TextWriter output,
    TextWriter progress)
{
    private readonly object _gate = new();
    private readonly ForegroundTimeline _timeline = new(observerId, interval * 3);
    private bool _sleeping;
    private bool _stopped;

    internal void Observe(ForegroundApplicationReading reading)
    {
        lock (_gate)
        {
            if (_sleeping || _stopped) return;
            var updates = _timeline.Accept(applications.Resolve(reading), reading);
            var active = updates[^1];
            store.Checkpoint(active, updates.Count == 2 ? updates[0] : null);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                type = "checkpoint",
                observerId = observerId.Value,
                localApplicationId = active.Application.Id.Value,
                identifiers = active.Application.Identifiers,
                contentId = active.Content.Id.Value,
                observationId = active.ObservationId.Value,
                startAt = active.StartAt,
                endAt = active.EndAt,
                active.Content.Name,
            }, JsonSerializerOptions.Web));
        }
    }

    internal void Poll()
    {
        lock (_gate)
        {
            if (_sleeping || _stopped) return;
            ForegroundApplicationReading reading;
            try { reading = read(); }
            catch (InvalidOperationException exception)
            {
                Interrupt();
                progress.WriteLine($"Foreground reading failed; the timeline was interrupted: {exception.Message}");
                return;
            }
            Observe(reading);
        }
    }

    internal void Sleep()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _sleeping = true;
            Interrupt();
        }
    }

    internal void Wake()
    {
        lock (_gate)
        {
            _sleeping = false;
            Poll();
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            Interrupt();
        }
    }

    private void Interrupt()
    {
        // Close only the durable checkpoint, including after an interrupted write.
        store.FinishActive();
        _timeline.Interrupt();
    }

    internal async Task RunAsync(int samples, CancellationToken cancellationToken)
    {
        for (var attempt = 0; samples == 0 || attempt < samples; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            Poll();
            if (samples != 0 && attempt + 1 == samples) break;
            var remaining = interval - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
        }
    }
}
