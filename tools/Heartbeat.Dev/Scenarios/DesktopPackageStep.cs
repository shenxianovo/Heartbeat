using System.Text.Json;
namespace Heartbeat.Dev;

internal sealed class DesktopPackageStep(RepositoryContext repository, IProcessRunner runner)
{
    public async Task<(string Executable, string Identifier, string DisplayName)> BuildAsync(EvidenceSession evidence, CancellationToken cancellationToken)
    {
        var package = Path.Combine(evidence.Run.Directory, "desktop-package");
        evidence.Commands.Add($"dotnet run --project tools/Heartbeat.Dev -- package desktop --output \"{package}\"");
        await using var log = new StreamWriter(Path.Combine(evidence.Run.Directory, "desktop-build.log"));
        var result = await new DesktopPackager(repository, runner, log)
            .PackageAsync(DesktopPackageOptions.Create(repository, output: package), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("Desktop package failed; see desktop-build.log.");
        var contents = Path.Combine(result.ApplicationPath, "Contents");
        // The SDK can produce binary plists. Convert on stdout to preserve the signed bundle.
        var identity = await runner.CaptureAsync("/usr/bin/plutil",
            ["-convert", "json", "-o", "-", Path.Combine(contents, "Info.plist")], null, cancellationToken);
        if (identity.ExitCode != 0) throw new InvalidDataException($"Cannot read application identity: {identity.StdErr.Trim()}");
        using var properties = JsonDocument.Parse(identity.StdOut);
        string Read(string key) => properties.RootElement.GetProperty(key).GetString()!;
        return (Path.Combine(contents, "MacOS", Read("CFBundleExecutable")), Read("CFBundleIdentifier"), Read("CFBundleDisplayName"));
    }
}
