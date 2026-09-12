using System.Text.Json;

namespace Heartbeat.Collector.Desktop.Mac;

internal sealed class DesktopCollectorSession(
    IForegroundApplicationReader reader,
    HeartbeatRecordingClient client,
    TimeProvider timeProvider)
{
    public async Task RunAsync(Guid trackId, CollectorOptions options, CancellationToken cancellationToken)
    {
        var batcher = new ForegroundRecordBatcher(timeProvider);
        var pending = new PendingForegroundRecords();
        Sample();
        if (options.Once)
        {
            await UploadPendingAsync(cancellationToken);
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampling = Task.Run(SampleAsync, CancellationToken.None);
        var uploading = Task.Run(UploadAsync, CancellationToken.None);
        await Task.WhenAll(sampling, uploading);

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

        async Task UploadAsync()
        {
            try
            {
                while (true)
                {
                    await UploadPendingAsync(stop.Token);
                    await Task.Delay(options.Interval, timeProvider, stop.Token);
                }
            }
            finally
            {
                await stop.CancelAsync();
            }
        }

        async Task UploadPendingAsync(CancellationToken token)
        {
            foreach (var record in pending.ReadBatch())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await client.UploadAsync(trackId, options.Target, record, token);
                    pending.Confirm(record);
                    Console.WriteLine($"{record.EndedAt:O} {record.Application.IdKind}:{record.Application.Id}");
                }
                catch (Exception exception) when (!options.Once && !token.IsCancellationRequested &&
                    exception is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
                {
                    Console.Error.WriteLine($"Upload failed; keeping Record {record.Id} for retry: {exception.Message}");
                }
            }
        }
    }
}
