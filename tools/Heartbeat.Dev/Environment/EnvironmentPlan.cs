namespace Heartbeat.Dev;

internal enum EnvironmentAction
{
    Up,
    Logs,
    Status,
    Down,
    Reset,
}

internal sealed record EnvironmentOptions(
    EnvironmentAction Action,
    bool Release,
    string? EnvironmentFile,
    bool Json,
    bool Apply,
    IReadOnlySet<string> RequestedServices);

internal sealed record EnvironmentPlan(
    EnvironmentOptions Options,
    IReadOnlyList<string> ComposeServices,
    bool RunDesktop)
{
    internal static readonly string[] AllowedServices =
        ["web", "api", "db", "hub", "desktop"];

    public static EnvironmentPlan Create(EnvironmentOptions options)
    {
        var requested = new HashSet<string>(options.RequestedServices, StringComparer.OrdinalIgnoreCase);
        foreach (var service in requested)
            if (!AllowedServices.Contains(service))
                throw new CommandUsageException($"Unknown env service '{service}'.");
        if (options.Action == EnvironmentAction.Reset && requested.Count > 0)
            throw new CommandUsageException("env reset always targets the whole local stack and does not accept services.");
        if (options.Action != EnvironmentAction.Up && requested.Contains("desktop"))
            throw new CommandUsageException("desktop is a macOS application; view its status and quit from Heartbeat Dev's window or menu bar.");
        requested = DefaultSelection(options.Action, requested);
        var selected = ExpandDependencies(options.Action, requested);
        return new EnvironmentPlan(options with { RequestedServices = requested },
            SelectComposeServices(options.Action, selected), selected.Contains("desktop"));
    }

    private static HashSet<string> DefaultSelection(EnvironmentAction action, HashSet<string> requested)
    {
        if (requested.Count == 0 && action != EnvironmentAction.Reset)
        {
            requested.UnionWith(["web", "api", "db"]);
        }
        return requested;
    }

    private static HashSet<string> ExpandDependencies(EnvironmentAction action, HashSet<string> requested)
    {
        var selected = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        if (action != EnvironmentAction.Up) return selected;
        if (selected.Contains("web")) selected.Add("api");
        if (selected.Contains("api")) selected.Add("db");
        return selected;
    }

    private static List<string> SelectComposeServices(
        EnvironmentAction action,
        HashSet<string> selected)
    {
        if (action == EnvironmentAction.Up) return SelectForUp(selected);
        var services = new List<string>();
        AddIfSelected(services, selected, "web");
        AddIfSelected(services, selected, "api");
        if (action == EnvironmentAction.Down && selected.Contains("api")) services.Add("migrate");
        AddIfSelected(services, selected, "db");
        AddIfSelected(services, selected, "hub");
        return services;
    }

    private static List<string> SelectForUp(HashSet<string> selected)
    {
        var services = new List<string>();
        AddIfSelected(services, selected, "db");
        if (selected.Contains("api")) services.AddRange(["migrate", "api"]);
        AddIfSelected(services, selected, "web");
        AddIfSelected(services, selected, "hub");
        return services;
    }

    private static void AddIfSelected(List<string> result, HashSet<string> selected, string service)
    {
        if (selected.Contains(service)) result.Add(service);
    }

}
