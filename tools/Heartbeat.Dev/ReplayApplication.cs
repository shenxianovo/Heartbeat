namespace Heartbeat.Dev;

internal static class ReplayApplication
{
    public const string Identity = "dev.heartbeat.verification.replay";
    public const string DisplayName = "Heartbeat Replay Probe";

    public static async Task<ScenarioProcess> StartAsync(RepositoryContext repository, IProcessRunner runner,
        EvidenceSession evidence, CancellationToken cancellationToken)
    {
        var contents = Path.Combine(evidence.Run.Directory, "ReplayProbe.app", "Contents");
        var binaries = Path.Combine(contents, "MacOS");
        Directory.CreateDirectory(binaries);
        await File.WriteAllTextAsync(Path.Combine(contents, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
            <key>CFBundleExecutable</key><string>ReplayProbe</string>
            <key>CFBundleIdentifier</key><string>{Identity}</string>
            <key>CFBundleName</key><string>{DisplayName}</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            </dict></plist>
            """, cancellationToken);
        var executable = Path.Combine(binaries, "ReplayProbe");
        evidence.Commands.Add("xcrun swiftc tools/Heartbeat.Dev/Fixtures/ReplayApp.swift (isolated .app)");
        var build = await runner.CaptureAsync("xcrun", ["swiftc", repository.Path("tools", "Heartbeat.Dev", "Fixtures", "ReplayApp.swift"),
            "-o", executable], null, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(evidence.Run.Directory, "replay-app-build.log"), build.StdOut + build.StdErr, cancellationToken);
        if (build.ExitCode != 0) throw new InvalidOperationException("Replay application build failed; see replay-app-build.log.");
        var ready = Path.Combine(evidence.Run.Directory, "foreground.ready");
        var process = new ScenarioProcess(repository.Root, executable, [ready]);
        try
        {
            await ScenarioWait.UntilAsync("the controlled application to become foreground", _ =>
            {
                if (process.Completion.IsCompleted) throw new InvalidOperationException("Replay application exited before becoming foreground.");
                return Task.FromResult(File.Exists(ready));
            }, cancellationToken);
            return process;
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }
    }
}
