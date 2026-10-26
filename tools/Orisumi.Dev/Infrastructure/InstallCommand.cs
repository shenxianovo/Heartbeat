using System.CommandLine;

namespace Orisumi.Dev;

internal sealed class InstallCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("install", "Pack this checkout and install or update the global orisumi tool");
        command.SetAction(async (_, token) =>
        {
            // A new version every time keeps the NuGet cache from reinstalling an older package.
            var version = $"0.1.0-dev.{DateTime.UtcNow.Ticks}";
            var packages = repository.Path(".artifacts", "packages");
            if (Directory.Exists(packages)) Directory.Delete(packages, recursive: true);
            var pack = await runner.CaptureAsync("dotnet", ["pack", repository.Path("tools", "Orisumi.Dev"),
                "--output", packages, $"-p:Version={version}"], null, token);
            await output.WriteAsync(pack.StdOut + pack.StdErr);
            if (pack.ExitCode != 0) return pack.ExitCode;
            // update also installs when the tool is absent.
            var install = await runner.CaptureAsync("dotnet", ["tool", "update", "--global", "Orisumi.Dev",
                "--add-source", packages, "--version", version, "--allow-downgrade"], null, token);
            await output.WriteAsync(install.StdOut + install.StdErr);
            return install.ExitCode;
        });
        return command;
    }
}
