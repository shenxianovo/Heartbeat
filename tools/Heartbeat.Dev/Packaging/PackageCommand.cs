using System.CommandLine;
using System.CommandLine.Help;

namespace Heartbeat.Dev;

internal sealed class PackageCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var group = new Command("package", "Build local application packages");
        group.SetAction(parse => new HelpAction().Invoke(parse));
        var desktop = new Command("desktop", "Package the native desktop app using the current platform SDK");
        var runtime = new Option<string?>("--runtime") { Description = "Target runtime (default: host OS and architecture)" };
        runtime.AcceptOnlyFromAmong(DesktopPackageOptions.SupportedRuntimes);
        var destination = new Option<string?>("--output") { Description = "Output parent directory; defaults to .artifacts/desktop or .artifacts/desktop-windows" };
        desktop.Options.Add(runtime);
        desktop.Options.Add(destination);
        desktop.SetAction(async (parse, token) =>
        {
            var options = DesktopPackageOptions.Create(repository, parse.GetValue(runtime), parse.GetValue(destination));
            var result = await new DesktopPackager(repository, runner, output).PackageAsync(options, token);
            if (result.ExitCode == 0) await output.WriteLineAsync($"Local application: {result.ApplicationPath}");
            return result.ExitCode;
        });
        group.Subcommands.Add(desktop);
        return group;
    }
}
