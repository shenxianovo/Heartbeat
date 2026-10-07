namespace Heartbeat.Dev;

internal enum EnvironmentAction { Up, Logs, Status, Down, Reset }

internal sealed record EnvironmentOptions(
    EnvironmentAction Action, bool Release, string? EnvironmentFile, bool Json, bool Apply,
    IReadOnlySet<string> RequestedServices);

internal sealed record EnvironmentPlan(
    EnvironmentOptions Options, IReadOnlyList<string> ComposeServices, bool WholeStack)
{
    internal static readonly string[] AllowedServices = ["all", "api", "docs", "db"];

    public static EnvironmentPlan Create(EnvironmentOptions options)
    {
        var requested = new HashSet<string>(options.RequestedServices, StringComparer.OrdinalIgnoreCase);
        foreach (var service in requested)
            if (!AllowedServices.Contains(service, StringComparer.OrdinalIgnoreCase))
                throw new CommandUsageException($"Unknown env target '{service}'.");
        if (requested.Contains("all") && requested.Count > 1)
            throw new CommandUsageException("'all' cannot be combined with individual targets.");
        var whole = requested.Count == 0 || requested.Contains("all");
        var services = new List<string>();
        if (!whole)
        {
            foreach (var service in AllowedServices.Skip(1))
                if (requested.Contains(service)) services.Add(service);
            if (options.Action == EnvironmentAction.Up && requested.Overlaps(["api", "docs"]))
                services.Add("nginx");
            if (options.Action is EnvironmentAction.Down or EnvironmentAction.Logs or EnvironmentAction.Status
                && requested.Contains("api")) services.Add("migrate");
        }
        return new EnvironmentPlan(options with { RequestedServices = requested }, services, whole);
    }
}
