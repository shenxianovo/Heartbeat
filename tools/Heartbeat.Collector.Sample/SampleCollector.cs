using System.Text.Json;
using Heartbeat.Contracts;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Sample;

public static class SampleCollector
{
    public static async Task<int> RunAsync(string target, IHubSubmissionClient hub, TextReader input,
        TextWriter output, TimeProvider clock, CancellationToken token = default)
    {
        var collector = new CollectorDeclaration("example.collector.manual", target, "Collector 接入演示");
        var pointRoute = new SubmissionRoute(collector, new("example.manual.confirmation", 1, "point"));
        var rangeRoute = new SubmissionRoute(collector, new("example.process.running", 1, "range"));
        ObjectReference[] objects = [new("source", "example.manual", target, "演示来源")];
        var pending = new PendingHubSubmissions();
        RecordSnapshot? current = null;
        var baseline = clock.GetUtcNow();
        var timestamp = clock.GetTimestamp();
        await output.WriteLineAsync("回车/observe：演示观测；gap：断采；retry：重试；quit/Ctrl+C：最终交接后退出。");
        try
        {
            // Console.In may block synchronously; cancel waiting without awaiting the reader at shutdown.
            while (await Task.Run(input.ReadLine, CancellationToken.None).WaitAsync(token) is { } line)
            {
                token.ThrowIfCancellationRequested();
                var command = line.Trim().ToLowerInvariant();
                if (command == "quit") break;
                if (command is "" or "observe")
                {
                    var at = baseline + clock.GetElapsedTime(timestamp, clock.GetTimestamp());
                    var point = new RecordSnapshot(Guid.CreateVersion7(at), at, null, null,
                        JsonSerializer.SerializeToElement(new { kind = "manual_confirmation" })) { Objects = objects };
                    // This demo treats confirmations at most five seconds apart as continuous.
                    // A real Collector must choose a rule justified by its observation source.
                    if (current is null || at - current.EndedAt > TimeSpan.FromSeconds(5))
                        current = new(Guid.CreateVersion7(at), at, at, null,
                            JsonSerializer.SerializeToElement(new { status = "running" })) { Objects = objects };
                    else current = current with { EndedAt = at };
                    pending.Stage(pointRoute, point);
                    pending.Stage(rangeRoute, current);
                    await output.WriteLineAsync($"Point {point.Id}; Range {current.Id} [{current.StartedAt:O}, {at:O}]");
                }
                else if (command == "gap") current = null;
                else if (command != "retry")
                {
                    await output.WriteLineAsync("未知命令。使用 observe、gap、retry 或 quit。");
                    continue;
                }
                await FlushAsync(pending, hub, output, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }

        // Stopping does not extend the last observed Range or manufacture another Point.
        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (await FlushAsync(pending, hub, output, final.Token)) return 0;
        var remaining = pending.ReadBatches().Sum(batch => batch.Records.Count);
        await output.WriteLineAsync($"退出时仍有 {remaining} 条快照未被 Hub 接管；它们仅在内存中，退出会丢失。");
        return 1;
    }

    private static async Task<bool> FlushAsync(PendingHubSubmissions pending, IHubSubmissionClient hub,
        TextWriter output, CancellationToken token)
    {
        try
        {
            foreach (var batch in pending.ReadBatches())
            {
                await hub.SubmitAsync(batch.ToSubmission(), token);
                pending.Confirm(batch);
                await output.WriteLineAsync($"Hub 已接管 {batch.Records.Count} 条快照；不代表后端已落库。");
            }
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            await output.WriteLineAsync($"交接未确认（{error.GetType().Name}），保留原快照。恢复连接/配置后输入 retry。");
            return false;
        }
    }
}
