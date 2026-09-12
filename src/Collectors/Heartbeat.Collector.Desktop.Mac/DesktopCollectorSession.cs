using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac;

internal sealed class DesktopCollectorSession(
    IForegroundApplicationReader reader,
    HubSubmissionClient client,
    TimeProvider timeProvider)
{
    private const string CollectorKey = "heartbeat.collector.desktop.macos";
    private const string TrackType = "desktop.application.foreground";

    public async Task RunAsync(CollectorOptions options, CancellationToken cancellationToken)
    {
        var batcher = new ForegroundRecordBatcher(timeProvider);
        var pending = new PendingForegroundRecords();
        Sample();
        if (options.Once)
        {
            await SubmitPendingAsync(cancellationToken);
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampling = Task.Run(SampleAsync, CancellationToken.None);
        var submitting = Task.Run(SubmitAsync, CancellationToken.None);
        await Task.WhenAll(sampling, submitting);

        void Sample()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = batcher.Confirm(reader.Read());
            if (record is not null)
            {
                pending.Stage(record);
            }
        }

        async Task SampleAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(options.Interval, timeProvider);
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    Sample();
                }
            }
            finally
            {
                await stop.CancelAsync();
            }
        }

        async Task SubmitAsync()
        {
            try
            {
                while (true)
                {
                    await SubmitPendingAsync(stop.Token);
                    await Task.Delay(options.Interval, timeProvider, stop.Token);
                }
            }
            finally
            {
                await stop.CancelAsync();
            }
        }

        async Task SubmitPendingAsync(CancellationToken token)
        {
            foreach (var record in pending.ReadBatch())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await client.SubmitAsync(new HubSubmission(
                        new CollectorDeclaration(CollectorKey, options.Target, options.DisplayName),
                        new TrackDeclaration(TrackType, 1, "range", "explicit"),
                        [record.ToSnapshot(options.Target)]), token);
                    pending.Confirm(record);
                    Console.WriteLine($"{record.EndedAt:O} {record.Application.IdKind}:{record.Application.Id}");
                }
                catch (Exception exception) when (!options.Once && !token.IsCancellationRequested &&
                    exception is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException)
                {
                    Console.Error.WriteLine($"Hub submission failed; keeping Record {record.Id} for retry: {exception.Message}");
                }
            }
        }
    }
}
