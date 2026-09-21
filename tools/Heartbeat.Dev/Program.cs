namespace Heartbeat.Dev;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var repository = await RepositoryContext.DiscoverAsync(Environment.CurrentDirectory);
            var runner = new ProcessRunner(repository.Root);
            var application = new DeveloperCli(repository, runner, Console.Out, Console.Error);
            return await application.RunAsync(args, cancellation.Token);
        }
        catch (CommandUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Run dotnet run --project tools/Heartbeat.Dev -- --help for usage.");
            return 2;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
