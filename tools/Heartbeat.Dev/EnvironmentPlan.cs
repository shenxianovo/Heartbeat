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
    private static readonly HashSet<string> AllowedServices =
        ["web", "api", "db", "hub", "desktop"];

    public static EnvironmentPlan Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help")
        {
            throw new CommandUsageException(EnvironmentCommand.HelpText);
        }

        var action = ParseAction(args[0]);
        var parsed = ParseArguments(args);
        Validate(action, parsed);
        var requested = DefaultSelection(action, parsed.Services);
        var selected = ExpandDependencies(action, requested);
        var options = new EnvironmentOptions(
            action, parsed.Release, parsed.EnvironmentFile, parsed.Json, parsed.Apply, requested);
        return new EnvironmentPlan(options, SelectComposeServices(action, selected), selected.Contains("desktop"));
    }

    private static EnvironmentAction ParseAction(string value) => value switch
    {
        "up" => EnvironmentAction.Up,
        "logs" => EnvironmentAction.Logs,
        "status" => EnvironmentAction.Status,
        "down" => EnvironmentAction.Down,
        "reset" => EnvironmentAction.Reset,
        _ => throw new CommandUsageException($"Unknown env action '{value}'."),
    };

    private static ParsedEnvironmentArguments ParseArguments(IReadOnlyList<string> args)
    {
        var parsed = new ParsedEnvironmentArguments();
        for (var index = 1; index < args.Count; index++)
        {
            index = ParseArgument(args, index, parsed);
        }
        return parsed;
    }

    private static int ParseArgument(
        IReadOnlyList<string> args,
        int index,
        ParsedEnvironmentArguments parsed)
    {
        switch (args[index])
        {
            case "--release": parsed.Release = true; break;
            case "--json": parsed.Json = true; break;
            case "--apply": parsed.Apply = true; break;
            case "--env-file":
                if (++index >= args.Count) throw new CommandUsageException("Missing path for '--env-file'.");
                parsed.EnvironmentFile = args[index];
                break;
            default:
                AddService(parsed.Services, args[index]);
                break;
        }
        return index;
    }

    private static void AddService(HashSet<string> services, string value)
    {
        if (!AllowedServices.Contains(value))
        {
            throw new CommandUsageException($"Unknown env option or service '{value}'.");
        }
        services.Add(value);
    }

    private static void Validate(EnvironmentAction action, ParsedEnvironmentArguments parsed)
    {
        ValidateOption(action, parsed.Release, EnvironmentAction.Up, "--release is only valid with 'env up'.");
        ValidateOption(action, parsed.Json, EnvironmentAction.Status, "--json is only valid with 'env status'.");
        ValidateOption(action, parsed.Apply, EnvironmentAction.Reset, "--apply is only valid with 'env reset'.");
        ValidateReset(action, parsed.Services);
        ValidateDesktop(action, parsed.Services);
    }

    private static void ValidateOption(
        EnvironmentAction action,
        bool present,
        EnvironmentAction validAction,
        string message)
    {
        if (present && action != validAction) throw new CommandUsageException(message);
    }

    private static void ValidateReset(EnvironmentAction action, HashSet<string> services)
    {
        if (action == EnvironmentAction.Reset && services.Count > 0)
            throw new CommandUsageException("env reset always targets the whole local stack and does not accept services.");
    }

    private static void ValidateDesktop(EnvironmentAction action, HashSet<string> services)
    {
        if (action != EnvironmentAction.Up && services.Contains("desktop"))
            throw new CommandUsageException("desktop is a foreground process; stop it with Ctrl+C where it is running.");
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
        if (selected.Contains("desktop")) selected.Add("hub");
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

    private sealed class ParsedEnvironmentArguments
    {
        public bool Release { get; set; }
        public bool Json { get; set; }
        public bool Apply { get; set; }
        public string? EnvironmentFile { get; set; }
        public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
