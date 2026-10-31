using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal static class Program
{
    public static async Task<int> Main()
    {
        await using var session = new PipeObserverSession(Console.In, Console.Out);
        try
        {
            var init = await session.Initialization;
            if (init.GetProperty("protocol").GetInt32() != 1) throw new InvalidDataException("Unsupported Runtime protocol.");
            var directory = init.GetProperty("dataDirectory").GetString()!;
            using var state = new AssistantState(directory);
            await state.ResumeAsync(session, session.Stopping);
            var registration = await AssistantRegistration.RegisterAsync(session, init.GetProperty("packageVersion").GetString()!, session.Stopping);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var collector = new AssistantCollector(session, state, new EntityId(init.GetProperty("instanceId").GetGuid()), registration,
                Path.Combine(home, ".claude"), Path.Combine(home, ".codex"), Path.Combine(directory, "archives"), Console.Error.WriteLine);
            await session.RequestAsync("ready", JsonLines.Json(new { }), cancellationToken: session.Stopping);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do { await collector.PollAsync(session.Stopping); }
            while (await timer.WaitForNextTickAsync(session.Stopping));
            return 0;
        }
        catch (OperationCanceledException) when (session.Stopping.IsCancellationRequested) { return 0; }
        catch (Exception exception) { await Console.Error.WriteLineAsync(exception.Message); return 1; }
    }
}
