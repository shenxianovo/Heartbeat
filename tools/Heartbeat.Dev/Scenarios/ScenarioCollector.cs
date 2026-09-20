namespace Heartbeat.Dev;

internal static class ScenarioCollector
{
    public static async Task<ScenarioProcess> StartAsync(RepositoryContext repository, IProcessRunner runner,
        EvidenceSession evidence, Uri hub, string token, string target, bool once,
        bool includeSensitiveEvidence, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("Native Collector scenarios require a logged-in macOS desktop.");
        var assembly = await CollectorBuild.EnsureAsync(repository, runner, evidence.Run, evidence.Commands, cancellationToken);
        string[] arguments = [assembly, "--interval-seconds", "1", "--maximum-gap-seconds", "3", "--window-title-dwell-ms", "1500"];
        if (once) arguments = [.. arguments, "--once"];
        evidence.Commands.Add($"dotnet {string.Join(' ', arguments)}");
        var environment = new Dictionary<string, string?>
        {
            ["HEARTBEAT_HUB_URL"] = hub.GetLeftPart(UriPartial.Authority),
            ["HEARTBEAT_HUB_TOKEN"] = token,
            ["HEARTBEAT_COLLECTOR_TARGET"] = target,
            ["HEARTBEAT_COLLECTOR_DISPLAY_NAME"] = "Scenario Collector",
            ["HEARTBEAT_COLLECTOR_ONCE"] = once.ToString(),
        };
        return new ScenarioProcess(repository.Root, "dotnet", arguments, environment,
            includeSensitiveEvidence ? Path.Combine(evidence.Run.Directory, "collector.log") : null);
    }
}
