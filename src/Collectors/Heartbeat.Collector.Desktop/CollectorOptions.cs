using System.Globalization;

namespace Heartbeat.Collector.Desktop;

public sealed record CollectorOptions(
    Uri HubBaseUrl,
    string HubToken,
    string Target,
    string DisplayName,
    TimeSpan Interval,
    TimeSpan MaximumConfirmationGap,
    TimeSpan WindowTitleDwell,
    bool Once)
{
    public DesktopCollectionOptions Collection => new(Target, DisplayName, Interval, MaximumConfirmationGap, WindowTitleDwell, Once);

    public static CollectorOptions Parse(string[] args, IDictionary<string, string?> environment)
    {
        var values = ParseArgs(args);
        var token = Required(values, environment, "hub-token", "HEARTBEAT_HUB_TOKEN");
        var target = Required(values, environment, "target", "HEARTBEAT_COLLECTOR_TARGET");
        var displayName = Get(values, environment, "display-name", "HEARTBEAT_COLLECTOR_DISPLAY_NAME");
        var timing = ReadTiming(values, environment);

        return new CollectorOptions(
            HubOrigin(Required(values, environment, "hub", "HEARTBEAT_HUB_URL")),
            token.Trim(),
            target.Trim(),
            (string.IsNullOrWhiteSpace(displayName) ? target : displayName).Trim(),
            timing.Interval,
            timing.MaximumConfirmationGap,
            timing.WindowTitleDwell,
            values.ContainsKey("once") || IsTruthy(environment.TryGetValue("HEARTBEAT_COLLECTOR_ONCE", out var once) ? once : null));
    }

    /// <summary>Hub 端点只接受 HTTP(S) 源，路径、查询、凭证都不属于端点的一部分。</summary>
    private static Uri HubOrigin(string hub)
    {
        var baseUrl = new Uri(EnsureTrailingSlash(hub.Trim()), UriKind.Absolute);
        return baseUrl.Scheme is ("http" or "https") && baseUrl.UserInfo.Length == 0 &&
               baseUrl.AbsolutePath == "/" && baseUrl.Query.Length == 0 && baseUrl.Fragment.Length == 0
            ? baseUrl
            : throw new ArgumentException("The Hub endpoint must be an HTTP(S) origin.");
    }

    /// <summary>三个时间参数互相约束，一起读、一起校验。</summary>
    private static Timing ReadTiming(Dictionary<string, string?> values, IDictionary<string, string?> environment)
    {
        var intervalSeconds = Number(values, environment, "interval-seconds", "HEARTBEAT_COLLECTOR_INTERVAL_SECONDS", 5);
        // One late tick is normal scheduling jitter, not an observation gap; declare the gap after
        // two consecutive missed confirmations.
        var maximumGapSeconds = Number(values, environment, "maximum-gap-seconds",
            "HEARTBEAT_COLLECTOR_MAXIMUM_GAP_SECONDS", checked(intervalSeconds * 3));
        // 实测：终端 spinner 一帧最长 1.05 秒，浏览器导航中间态更短。阈值要高过最慢的那一帧才压得住，
        // 又要远低于真实标题变更的停留时长。0 表示关掉静置，每次标题变化都立刻承认。
        var dwellMilliseconds = Number(values, environment, "window-title-dwell-ms",
            "HEARTBEAT_COLLECTOR_WINDOW_TITLE_DWELL_MS", 1500);

        if (intervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "Interval must be at least 1 second.");
        }
        if (maximumGapSeconds <= intervalSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "Maximum confirmation gap must exceed the sampling interval.");
        }
        if (dwellMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "Window title dwell cannot be negative.");
        }
        // 静置比中断阈值还长，候选就会先被中断清掉，永远没有转正的机会。
        if (dwellMilliseconds >= maximumGapSeconds * 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "Window title dwell must be shorter than the maximum confirmation gap.");
        }

        return new Timing(
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(maximumGapSeconds),
            TimeSpan.FromMilliseconds(dwellMilliseconds));
    }

    private static int Number(
        Dictionary<string, string?> values,
        IDictionary<string, string?> environment,
        string option,
        string variable,
        int fallback)
    {
        var value = Get(values, environment, option, variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string Required(
        Dictionary<string, string?> values,
        IDictionary<string, string?> environment,
        string option,
        string variable)
    {
        var value = Get(values, environment, option, variable);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Missing --{option} or {variable}.")
            : value;
    }

    private sealed record Timing(TimeSpan Interval, TimeSpan MaximumConfirmationGap, TimeSpan WindowTitleDwell);

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hub", "hub-token", "target", "display-name", "interval-seconds", "maximum-gap-seconds",
            "window-title-dwell-ms", "once",
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
