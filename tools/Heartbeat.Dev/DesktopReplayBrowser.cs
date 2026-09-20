namespace Heartbeat.Dev;

internal static class DesktopReplayBrowser
{
    public static async Task RunAsync(RepositoryContext repository, EvidenceSession evidence, Uri web, CancellationToken cancellationToken)
    {
        var workspace = await WebVerificationWorkspace.PrepareAsync(repository, evidence.Run, cancellationToken);
        var arguments = new[] { Path.Combine(workspace, "tests", "scenarios", "desktop-replay.ts"),
            evidence.Run.Directory, web.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        evidence.Commands.Add("node tests/scenarios/desktop-replay.ts (isolated Web, real OIDC)");
        await using var browser = new ScenarioProcess(workspace, "node", arguments,
            logPath: Path.Combine(evidence.Run.Directory, "browser.log"));
        var result = await browser.WaitAsync(TimeSpan.FromMinutes(7), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("Browser replay failed; see replay.json for the failing stage.");
    }
}
