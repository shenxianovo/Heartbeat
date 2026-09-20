using System.Text.Json;
using System.Xml.Linq;
using Heartbeat.Desktop;
using Heartbeat.Hub;

namespace Heartbeat.Dev;

internal sealed class DesktopReplayScenario(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Task<int> RunAsync(ScenarioOptions options, CancellationToken cancellationToken) =>
        ScenarioEnvironment.RunAsync(repository, runner, output, options,
        [
            "Packaged macOS application, real settings/Keychain, native Collector, in-process Hub, isolated backend and production Web.",
            "Interactive: configure the desktop, start collection, then pause and quit when prompted; sign in to real OIDC in Chromium.",
            "Does not verify Windows, automatic updates, distribution signing, permissions, lock/sleep or long-running stability.",
        ], environment => RunAsync(environment, cancellationToken), cancellationToken);

    private async Task<int> RunAsync(ScenarioEnvironment environment, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("desktop-replay requires macOS.");
        var profile = Directory.CreateTempSubdirectory("heartbeat-desktop-scenario-").FullName;
        var cleaned = false;
        try
        {
            await environment.StartAsync(cancellationToken, "web");
            var package = await BuildPackageAsync(environment.Evidence, cancellationToken);
            var started = DateTimeOffset.UtcNow;
            await using var desktop = new ScenarioProcess(repository.Root, package.Executable, ["--data-directory", profile]);
            var auth = DotenvFile.Read(repository.Path(".env.local")).Get("AUTH_AUTHORITY") ?? "https://auth.shenxianovo.com";
            await environment.WriteAsync("desktop-setup.json", new { profile, backend = environment.Web, auth, web = environment.Web }, cancellationToken);
            await output.WriteLineAsync($"Desktop setup: backend and Web = {environment.Web}, Auth = {auth}. Use your API key, save, then start collection. Private profile: {profile}");
            await ScenarioWait.UntilAsync("desktop connection and collection", _ =>
            {
                if (desktop.Completion.IsCompleted) throw new InvalidOperationException("Desktop exited before collection started.");
                return Task.FromResult(File.Exists(Path.Combine(profile, "hub.sqlite")));
            }, cancellationToken, TimeSpan.FromMinutes(10));
            var settings = JsonSerializer.Deserialize<DesktopSettings>(await File.ReadAllTextAsync(Path.Combine(profile, "settings.json"), cancellationToken))!;
            if (settings.OwnerId != environment.Owner) throw new InvalidOperationException("Desktop Owner does not match the scenario Owner.");
            var queue = new RecordOutbox(Path.Combine(profile, "hub.sqlite"), settings.Destination);
            DesktopReplayEvidence? witness = null;
            await output.WriteLineAsync("Keep this Heartbeat Dev window in the foreground until the next prompt. The scenario verifies the real client's own application Record.");
            await ScenarioWait.UntilAsync("the desktop client's application Record", async token =>
            {
                if (desktop.Completion.IsCompleted) throw new InvalidOperationException("Desktop exited before its Record was delivered.");
                var json = await environment.QueryDatabaseAsync(
                    DesktopReplayEvidence.Query(environment.Owner, settings.Target, package.Identifier, started, DateTimeOffset.UtcNow), token);
                witness = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<DesktopReplayEvidence>(json);
                return witness is not null;
            }, cancellationToken);
            await output.WriteLineAsync("Client Record delivered. In Heartbeat Dev: pause collection, wait for 0 pending / 0 failed, then choose Quit Heartbeat Dev from the menu bar.");
            var stopped = await desktop.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken);
            if (stopped.ExitCode != 0) throw new InvalidOperationException("Desktop did not exit cleanly.");
            var custody = queue.Status();
            await environment.WriteAsync("desktop-custody.json", custody, cancellationToken);
            if (custody.Pending != 0 || custody.Failed != 0) throw new InvalidOperationException("Pause and let Hub finish delivery before quitting.");
            var finalJson = await environment.QueryDatabaseAsync(
                DesktopReplayEvidence.Query(environment.Owner, settings.Target, package.Identifier, started, DateTimeOffset.UtcNow), cancellationToken);
            var delivered = JsonSerializer.Deserialize<DesktopReplayEvidence>(finalJson)!;
            delivered.RequireContinuationOf(witness!);
            await environment.WriteAsync("replay-expectation.json", new { record = delivered, target = settings.Target,
                applicationId = package.Identifier, applicationName = package.DisplayName }, cancellationToken);
            await output.WriteLineAsync("Opening Chromium for real OIDC login and verification of the same Record in Web replay.");
            await DesktopReplayBrowser.RunAsync(repository, environment.Evidence, environment.Web, cancellationToken);
        }
        finally
        {
            // This credential belongs only to the temporary scenario profile.
            var cleanup = await runner.CaptureAsync("/usr/bin/security", ["delete-generic-password", "-s",
                DesktopProfile.CredentialService, "-a", DesktopProfile.CredentialAccount(profile)], null, CancellationToken.None);
            Directory.Delete(profile, recursive: true);
            cleaned = cleanup.ExitCode is 0 or 44;
            if (!cleaned) await output.WriteLineAsync("Temporary desktop Keychain cleanup was not confirmed.");
        }
        return cleaned ? 0 : 1;
    }

    private async Task<(string Executable, string Identifier, string DisplayName)> BuildPackageAsync(EvidenceSession evidence, CancellationToken cancellationToken)
    {
        var package = Path.Combine(evidence.Run.Directory, "desktop-package");
        evidence.Commands.Add("scripts/package-desktop-mac.sh (isolated package output)");
        var result = await runner.CaptureAsync("/bin/bash", [repository.Path("scripts", "package-desktop-mac.sh"), package], null, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(evidence.Run.Directory, "desktop-build.log"), result.StdOut + result.StdErr, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("Desktop package failed; see desktop-build.log.");
        var contents = Path.Combine(package, "Heartbeat Dev.app", "Contents");
        var properties = XDocument.Load(Path.Combine(contents, "Info.plist")).Root!.Element("dict")!;
        string Read(string key) => properties.Elements("key").Single(item => item.Value == key).ElementsAfterSelf().First().Value;
        return (Path.Combine(contents, "MacOS", Read("CFBundleExecutable")), Read("CFBundleIdentifier"), Read("CFBundleDisplayName"));
    }
}
