using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Dev;

internal static class AuthenticatedReplayBrowser
{
    internal sealed record Session(string Authority, string ClientId, BackendAccessToken Token);

    public static async Task<Session> AuthenticateAsync(ScenarioConfiguration configuration, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var provider = new ApiKeyTokenProvider(http, new Uri(configuration.Authority), configuration.ApiKey);
        var access = await provider.GetTokenAsync(token)
            ?? throw new InvalidOperationException($"Recovery Auth preflight failed: {provider.LastError}");
        if (access.OwnerId != configuration.Owner) throw new InvalidOperationException("Recovery Auth token belongs to a different Owner.");
        return new(configuration.Authority, configuration.ClientId, access);
    }

    public static async Task RunAsync(RepositoryContext repository, ScenarioEnvironment environment,
        RecoveryRecord[] records, CancellationToken token)
    {
        // Builds can outlive a token. Exchange again immediately before the browser starts.
        var session = await AuthenticateAsync(environment.Configuration, token);
        var workspace = await WebVerificationWorkspace.PrepareAsync(repository, environment.Evidence.Run, token);
        var arguments = new[] { Path.Combine(workspace, "tests", "scenarios", "authenticated-replay.ts"), environment.Evidence.Run.Directory };
        var input = JsonSerializer.Serialize(new { web = environment.Web, session, records }, JsonOptions.Indented);
        environment.Evidence.Commands.Add("node tests/scenarios/authenticated-replay.ts (real API, temporary Auth session)");
        await using var browser = new ScenarioProcess(workspace, "node", arguments,
            new Dictionary<string, string?> { ["HEARTBEAT_REPLAY_INPUT"] = input },
            Path.Combine(environment.Evidence.Run.Directory, "browser.log"));
        var result = await browser.WaitAsync(TimeSpan.FromMinutes(2), token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Recovery Web replay failed; see replay.json for the failing stage.");
    }
}
