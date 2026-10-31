using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal static class Program
{
    public static async Task<int> Main()
    {
        await using var data = DataClient.Connect();
        try
        {
            var directory = data.DataDirectory!;
            using var state = new AssistantState(directory);
            await AssistantRegistration.RegisterAsync(data, data.PackageVersion!, data.Stopping);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var collector = new AssistantCollector(data, state,
                Path.Combine(home, ".claude"), Path.Combine(home, ".codex"), Path.Combine(directory, "archives"), Console.Error.WriteLine);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do { await collector.PollAsync(data.Stopping); }
            while (await timer.WaitForNextTickAsync(data.Stopping));
            return 0;
        }
        catch (OperationCanceledException) when (data.Stopping.IsCancellationRequested) { return 0; }
        catch (Exception exception) { await Console.Error.WriteLineAsync(exception.Message); return 1; }
    }
}
