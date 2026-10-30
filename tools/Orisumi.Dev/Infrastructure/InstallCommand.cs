using System.CommandLine;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class InstallCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("install", "Pack this checkout and install or update the global orisumi tool");
        command.SetAction(async (_, token) =>
        {
            var console = Terminal.Create(output);
            // A new version every time keeps the NuGet cache from reinstalling an older package.
            var version = $"0.1.0-dev.{DateTime.UtcNow.Ticks}";
            var packages = repository.Path(".artifacts", "packages");
            if (Directory.Exists(packages)) Directory.Delete(packages, recursive: true);
            var pack = await Terminal.SpinAsync(console, "Packing Orisumi.Dev", () => runner.CaptureAsync("dotnet",
                ["pack", repository.Path("tools", "Orisumi.Dev"), "--output", packages, $"-p:Version={version}"], null, token));
            if (pack.ExitCode != 0) return Fail(console, "Packing failed", pack);
            // update also installs when the tool is absent.
            var install = await Terminal.SpinAsync(console, "Installing orisumi", () => runner.CaptureAsync("dotnet",
                ["tool", "update", "--global", "Orisumi.Dev", "--add-source", packages, "--version", version, "--allow-downgrade"],
                null, token));
            if (install.ExitCode != 0) return Fail(console, "Installing failed", install);
            console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} Installed orisumi [grey]{Terminal.Esc(version)}[/]");
            return 0;
        });
        return command;
    }

    private static int Fail(IAnsiConsole console, string message, ProcessResult result)
    {
        console.MarkupLine($"{Terminal.Mark(Outcome.Failed)} {Terminal.Esc(message)} [grey](exit {result.ExitCode})[/]");
        console.WriteLine(result.StdOut + result.StdErr);
        return result.ExitCode;
    }
}
