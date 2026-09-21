using System.CommandLine;
using System.CommandLine.Help;

namespace Heartbeat.Dev;

internal sealed class SigningCommand(IProcessRunner runner, TextWriter output, string? hostRuntime = null)
{
    public Command CreateCommand()
    {
        var group = new Command("signing", "Prepare and inspect development signing for this platform");
        group.SetAction(parse => new HelpAction().Invoke(parse));
        var setup = new Command("setup", "Create the development identity once, or reuse the existing identity");
        setup.SetAction(async (_, token) => await RunAsync(true, token));
        var status = new Command("status", "Check development signing requirements and identity (read only)");
        status.SetAction(async (_, token) => await RunAsync(false, token));
        group.Subcommands.Add(setup);
        group.Subcommands.Add(status);
        return group;
    }

    private async Task<int> RunAsync(bool setup, CancellationToken token)
    {
        var runtime = hostRuntime ?? System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        if (runtime.StartsWith("win-", StringComparison.Ordinal))
        {
            await output.WriteLineAsync("Windows development signing is not required for the unpackaged desktop app. No certificate was created or trusted. This does not bypass Windows security policy or provide distribution trust.");
            return 0;
        }
        if (!runtime.StartsWith("osx-", StringComparison.Ordinal))
            throw new CommandUsageException("Desktop development signing supports macOS and Windows.");
        var signing = new MacDevelopmentSigning(runner, output);
        if (setup) await signing.SetupAsync(token);
        else await signing.RequireIdentityAsync(token);
        return 0;
    }
}
