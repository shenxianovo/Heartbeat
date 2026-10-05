using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Dev;

internal static class DesktopReplayBrowser
{
    internal sealed record Session(string Authority, string ClientId, BackendAccessToken Token);

    public static async Task<Session> AuthenticateAsync(ScenarioConfiguration configuration, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var provider = new ApiKeyTokenProvider(http, new Uri(configuration.Authority), configuration.ApiKey);
        var access = await provider.GetTokenAsync(token)
            ?? throw new InvalidOperationException("Desktop replay Auth preflight failed.");
        if (access.OwnerId != configuration.Owner) throw new InvalidOperationException("Desktop replay token belongs to a different Owner.");
        return new(configuration.Authority, configuration.ClientId, access);
    }

    public static async Task<string> RunAsync(RepositoryContext repository, ScenarioEnvironment environment,
        DesktopReplayBatch batch, ReplayApplication application, Guid hubId, StageArtifacts artifacts, bool interactiveLogin, CancellationToken token)
    {
        // Builds can outlive a token. Exchange immediately before the automated browser starts.
        var session = interactiveLogin ? null : await AuthenticateAsync(environment.Configuration, token);
        var workspace = await WebVerificationWorkspace.PrepareAsync(repository, environment.Evidence.Run, token);
        var item = batch.Records.Single(item => item.Record.Id == batch.Witness.RecordId);
        var witness = new { record = batch.Witness, target = item.Target, collectorKey = application.CollectorKey, applicationId = application.Identifier,
            applicationName = application.DisplayName, applicationKind = "bundle_id" };
        var report = artifacts.File("replay.json");
        var input = JsonSerializer.Serialize(new { web = environment.Web, session, witness, hubId, records = batch.Records,
            files = new { report, screenshot = artifacts.File("record-time.png"), activityScreenshot = artifacts.File("hub-activity.png") } }, JsonOptions.Indented);
        var script = Path.Combine(workspace, "tests", "scenarios", "desktop-replay.ts");
        environment.Evidence.Commands.Add($"node tests/scenarios/desktop-replay.ts ({(interactiveLogin ? "interactive OIDC" : "temporary Auth session")})");
        await using var browser = new ScenarioProcess(workspace, "node", [script],
            new Dictionary<string, string?> { ["HEARTBEAT_REPLAY_INPUT"] = input }, artifacts.File("browser.log"));
        var timeout = interactiveLogin ? TimeSpan.FromMinutes(7) : TimeSpan.FromMinutes(2);
        var result = await browser.WaitAsync(timeout, token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Desktop Web replay failed; see {report}.");
        return report;
    }
}
