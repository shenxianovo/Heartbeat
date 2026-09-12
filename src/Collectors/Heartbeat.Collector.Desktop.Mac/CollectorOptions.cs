using System.Globalization;

namespace Heartbeat.Collector.Desktop.Mac;

public sealed record CollectorOptions(
    Uri HubBaseUrl,
    string HubToken,
    string Target,
    string DisplayName,
    TimeSpan Interval,
    bool Once)
{
    public static CollectorOptions Parse(string[] args, IDictionary<string, string?> environment)
    {
        var values = ParseArgs(args);
        var hub = Get(values, environment, "hub", "HEARTBEAT_HUB_URL");
        var token = Get(values, environment, "hub-token", "HEARTBEAT_HUB_TOKEN");
        var target = Get(values, environment, "target", "HEARTBEAT_COLLECTOR_TARGET");
        var displayName = Get(values, environment, "display-name", "HEARTBEAT_COLLECTOR_DISPLAY_NAME");
        var intervalValue = Get(values, environment, "interval-seconds", "HEARTBEAT_COLLECTOR_INTERVAL_SECONDS");
        var intervalSeconds = string.IsNullOrWhiteSpace(intervalValue)
            ? 5
            : int.Parse(intervalValue, CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(hub))
        {
            throw new ArgumentException("Missing --hub or HEARTBEAT_HUB_URL.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Missing --hub-token or HEARTBEAT_HUB_TOKEN.");
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("Missing --target or HEARTBEAT_COLLECTOR_TARGET.");
        }

        displayName = string.IsNullOrWhiteSpace(displayName) ? target : displayName;
        if (intervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Interval must be at least 1 second.");
        }

        var baseUrl = new Uri(EnsureTrailingSlash(hub.Trim()), UriKind.Absolute);
        if (baseUrl.Scheme is not ("http" or "https") || baseUrl.UserInfo.Length != 0 ||
            baseUrl.AbsolutePath != "/" || baseUrl.Query.Length != 0 || baseUrl.Fragment.Length != 0)
        {
            throw new ArgumentException("The Hub endpoint must be an HTTP(S) origin.");
        }

        return new CollectorOptions(baseUrl, token.Trim(), target.Trim(), displayName.Trim(),
            TimeSpan.FromSeconds(intervalSeconds),
            values.ContainsKey("once") || IsTruthy(environment.TryGetValue("HEARTBEAT_COLLECTOR_ONCE", out var once) ? once : null));
    }

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hub", "hub-token", "target", "display-name", "interval-seconds", "once",
        };
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(arg[2..]))
            {
                throw new ArgumentException($"Unknown argument '{arg}'.");
            }

            var name = arg[2..];
            if (name == "once")
            {
                values[name] = "true";
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{arg}'.");
            }

            values[name] = args[++index];
        }

        return values;
    }

    private static string? Get(Dictionary<string, string?> values, IDictionary<string, string?> environment,
        string option, string variable) => values.TryGetValue(option, out var value)
            ? value
            : environment.TryGetValue(variable, out value) ? value : null;

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : $"{value}/";

    private static bool IsTruthy(string? value) => value?.Trim().ToLowerInvariant() is "1" or "true" or "yes";
}
