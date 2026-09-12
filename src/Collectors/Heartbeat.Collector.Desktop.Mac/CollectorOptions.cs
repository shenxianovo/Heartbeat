using System.Globalization;

namespace Heartbeat.Collector.Desktop.Mac;

public sealed record CollectorOptions(
    Uri ApiBaseUrl,
    string AuthToken,
    string Target,
    string DisplayName,
    TimeSpan Interval,
    bool Once)
{
    public static CollectorOptions Parse(string[] args, IDictionary<string, string?> environment)
    {
        var values = ParseArgs(args);
        var api = Get(values, environment, "api", "HEARTBEAT_API_BASE_URL");
        var token = Get(values, environment, "token", "HEARTBEAT_AUTH_TOKEN");
        var target = Get(values, environment, "target", "HEARTBEAT_COLLECTOR_TARGET");
        var displayName = Get(values, environment, "display-name", "HEARTBEAT_COLLECTOR_DISPLAY_NAME");
        var intervalValue = Get(values, environment, "interval-seconds", "HEARTBEAT_COLLECTOR_INTERVAL_SECONDS");
        var intervalSeconds = string.IsNullOrWhiteSpace(intervalValue)
            ? 5
            : int.Parse(intervalValue, CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(api))
        {
            throw new ArgumentException("Missing --api or HEARTBEAT_API_BASE_URL.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Missing --token or HEARTBEAT_AUTH_TOKEN.");
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

        return new CollectorOptions(
            new Uri(EnsureTrailingSlash(api.Trim()), UriKind.Absolute),
            token.Trim(),
            target.Trim(),
            displayName.Trim(),
            TimeSpan.FromSeconds(intervalSeconds),
            values.ContainsKey("once") || IsTruthy(environment.TryGetValue("HEARTBEAT_COLLECTOR_ONCE", out var once) ? once : null));
    }

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
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

    private static string? Get(
        Dictionary<string, string?> values,
        IDictionary<string, string?> environment,
        string option,
        string variable)
        => values.TryGetValue(option, out var value)
            ? value
            : environment.TryGetValue(variable, out value)
                ? value
                : null;

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : $"{value}/";

    private static bool IsTruthy(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes";
}
