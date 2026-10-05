using System.Globalization;

namespace Heartbeat.Collector.Desktop.Mac.Diagnostics;

/// 探针的运行参数。探针只观察前台读数，不需要 Hub、Target 或凭据，
/// 因此它的参数与 CollectorOptions 完全分开，不共用必填校验。
internal sealed record WindowTitleProbeOptions(
    TimeSpan Duration,
    TimeSpan PollInterval,
    string OutputPath,
    bool IncludeTitles)
{
    public const string Flag = "--probe-window-titles";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Contains(Flag, StringComparer.Ordinal);

    public static WindowTitleProbeOptions Parse(IReadOnlyList<string> args)
    {
        var duration = TimeSpan.FromSeconds(120);
        var poll = TimeSpan.FromMilliseconds(250);
        string? output = null;
        var includeTitles = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case Flag:
                    break;
                case "--include-titles":
                    includeTitles = true;
                    break;
                case "--duration-seconds":
                    duration = TimeSpan.FromSeconds(Number(args, ++index, "--duration-seconds"));
                    break;
                case "--poll-milliseconds":
                    poll = TimeSpan.FromMilliseconds(Number(args, ++index, "--poll-milliseconds"));
                    break;
                case "--output":
                    output = Value(args, ++index, "--output");
                    break;
                default:
                    throw new ArgumentException($"Unknown probe argument '{args[index]}'.");
            }
        }

        return Validate(duration, poll, output, includeTitles);
    }

    private static WindowTitleProbeOptions Validate(
        TimeSpan duration,
        TimeSpan poll,
        string? output,
        bool includeTitles)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Probe duration must be between 1 second and 2 hours.");
        }
        if (poll < TimeSpan.FromMilliseconds(50) || poll > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(poll), "Probe poll interval must be between 50 ms and 10 s.");
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException("Missing --output for the probe readings file.");
        }

        return new WindowTitleProbeOptions(duration, poll, Path.GetFullPath(output.Trim()), includeTitles);
    }

    private static double Number(IReadOnlyList<string> args, int index, string option) =>
        double.Parse(Value(args, index, option), CultureInfo.InvariantCulture);

    private static string Value(IReadOnlyList<string> args, int index, string option) =>
        index < args.Count ? args[index] : throw new ArgumentException($"Missing value for '{option}'.");
}
