using System.CommandLine;
using System.Text.Json;
using Heartbeat.Desktop;

namespace Heartbeat.Dev;

internal sealed record RuntimeHostInput(string Profile, Uri Web, Uri Authority, string? ApiKey);

// Private child-process entry: no GUI, OS collection, HTTP control server or production hook.
internal static class RuntimeScenarioHost
{
    public const string CommandName = "runtime-host";
    public const string InputVariable = "HEARTBEAT_RUNTIME_SCENARIO";
    public static Command CreateCommand()
    {
        var command = new Command(CommandName) { Hidden = true };
        command.SetAction(async (_, token) =>
        {
            var input = Environment.GetEnvironmentVariable(InputVariable);
            Environment.SetEnvironmentVariable(InputVariable, null);
            try
            {
                await RunAsync(JsonSerializer.Deserialize<RuntimeHostInput>(input ?? "null")
                    ?? throw new InvalidOperationException("Missing scenario input."), token);
                return 0;
            }
            catch (Exception error)
            {
                // Neither credentials nor remote error bodies enter stdout/stderr evidence.
                await Console.Error.WriteLineAsync(error.GetType().Name);
                return 1;
            }
        });
        return command;
    }

    private static async Task RunAsync(RuntimeHostInput input, CancellationToken token)
    {
        var platform = new RuntimeScenarioPlatform(input.Profile);
        using var profile = new DesktopProfile(input.Profile, platform.Credentials);
        await using var runtime = new DesktopRuntime(profile, platform);
        if (runtime.Settings is null)
            await runtime.ConfigureAsync(input.Web, input.Authority, input.Web, input.ApiKey, token);
        else
        {
            await runtime.InitializeAsync();
            if (!runtime.IsCollecting) throw new InvalidOperationException("Saved profile did not restart collection.");
        }
        await Console.Out.WriteLineAsync("ready");
        while (await Console.In.ReadLineAsync(token) is { } command)
        {
            switch (command)
            {
                case "start": await runtime.StartAsync(); break;
                case "pause": await runtime.StopCollectionAsync(); break;
                case "quit": return;
                default: throw new InvalidOperationException("Unknown runtime action.");
            }
            await Console.Out.WriteLineAsync("ok");
        }
    }
}
