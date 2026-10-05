using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Collector.Desktop.Mac.Diagnostics;

/// 前台窗口标题探针。它不产生 Record，也不连接 Hub：只把原生通知与轮询读数按时间原样记录下来，
/// 用于回答「标题在真实使用中变化得多快」，再由离线分析决定切分规则。
/// 默认不保存标题原文，只保存指纹与形态度量；标题原文需要显式开启。
internal sealed class WindowTitleProbe(IDesktopObservationSource source, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly List<Reading> _readings = [];
    private readonly List<CapabilityEntry> _capabilities = [];
    private DesktopActivitySample? _previous;
    private bool _hasPrevious;

    public async Task<int> RunAsync(
        WindowTitleProbeOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        source.Observation += OnObservation;
        source.StartObserving();
        try
        {
            await PollUntilAsync(options, startedAt + options.Duration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping the probe early still yields usable readings.
        }
        finally
        {
            source.StopObserving();
            source.Observation -= OnObservation;
        }

        var completedAt = time.GetUtcNow();
        await WriteAsync(options, startedAt, completedAt);
        await output.WriteLineAsync(
            $"Probe recorded {_readings.Count} readings over {(completedAt - startedAt).TotalSeconds:F1}s to {options.OutputPath}.");
        return 0;
    }

    private async Task PollUntilAsync(
        WindowTitleProbeOptions options,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        Record("poll", source.Capture().Activity);
        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(options.PollInterval, time, cancellationToken);
            Record("poll", source.Capture().Activity);
        }
    }

    private void OnObservation(DesktopObservation observation)
    {
        switch (observation)
        {
            case DesktopObservation.Activity activity:
                Record("event", activity.Sample);
                break;
            case DesktopObservation.Capability capability:
                RecordCapability(capability.Value);
                break;
            default:
                break;
        }
    }

    private void RecordCapability(CapabilityObservation observation)
    {
        lock (_gate)
        {
            _capabilities.Add(new CapabilityEntry(
                time.GetUtcNow(), observation.Capability.ToString(), observation.State.ToString(), observation.Reason));
        }
    }

    private void Record(string origin, DesktopActivitySample? sample)
    {
        lock (_gate)
        {
            var at = time.GetUtcNow();
            var previous = _hasPrevious ? _previous : null;
            _readings.Add(Describe(at, origin, sample, previous));
            _previous = sample;
            _hasPrevious = true;
        }
    }

    private static Reading Describe(
        DateTimeOffset at,
        string origin,
        DesktopActivitySample? sample,
        DesktopActivitySample? previous)
    {
        var title = sample?.WindowTitle;
        var previousTitle = previous?.WindowTitle;
        var shape = TitleShape.Between(previousTitle, title);
        return new Reading(
            at,
            origin,
            sample?.Application.Id,
            sample?.Application.DisplayName,
            title is not null,
            title?.Length ?? 0,
            Fingerprint(title),
            title,
            sample?.Application.Id == previous?.Application.Id,
            string.Equals(previousTitle, title, StringComparison.Ordinal),
            shape.CommonPrefix,
            shape.CommonSuffix,
            shape.IsRotation);
    }

    private static string? Fingerprint(string? title) => title is null
        ? null
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title)).AsSpan(0, 8));

    private async Task WriteAsync(
        WindowTitleProbeOptions options,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        Reading[] readings;
        CapabilityEntry[] capabilities;
        lock (_gate)
        {
            readings = options.IncludeTitles
                ? [.. _readings]
                : [.. _readings.Select(reading => reading with { Title = null })];
            capabilities = [.. _capabilities];
        }

        var document = new ProbeDocument(
            startedAt,
            completedAt,
            (completedAt - startedAt).TotalSeconds,
            options.PollInterval.TotalMilliseconds,
            options.IncludeTitles,
            readings,
            capabilities);
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        await File.WriteAllTextAsync(
            options.OutputPath,
            JsonSerializer.Serialize(document, ProbeJson.Options),
            CancellationToken.None);
    }

    private sealed record ProbeDocument(
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        double DurationSeconds,
        double PollIntervalMilliseconds,
        bool IncludesTitles,
        IReadOnlyList<Reading> Readings,
        IReadOnlyList<CapabilityEntry> Capabilities);

    private sealed record Reading(
        DateTimeOffset At,
        string Origin,
        string? Application,
        string? ApplicationName,
        bool TitleAvailable,
        int TitleLength,
        string? TitleHash,
        string? Title,
        bool SameApplicationAsPrevious,
        bool SameTitleAsPrevious,
        int CommonPrefix,
        int CommonSuffix,
        bool RotationOfPrevious);

    private sealed record CapabilityEntry(DateTimeOffset At, string Capability, string State, string? Reason);
}

internal static class ProbeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// 相邻两个标题之间的形态关系。滚动字幕的相邻读数长度相同且互为旋转，
/// 进度类标题通常共享很长的前缀，这些形态不需要保存标题原文就能观察。
internal readonly record struct TitleShape(int CommonPrefix, int CommonSuffix, bool IsRotation)
{
    public static TitleShape Between(string? previous, string? current)
    {
        if (previous is null || current is null || previous.Length == 0 || current.Length == 0)
        {
            return new TitleShape(0, 0, false);
        }

        var prefix = CommonPrefixLength(previous, current);
        var suffix = CommonSuffixLength(previous, current, prefix);
        var rotation = previous.Length == current.Length
            && !string.Equals(previous, current, StringComparison.Ordinal)
            && (previous + previous).Contains(current, StringComparison.Ordinal);
        return new TitleShape(prefix, suffix, rotation);
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var limit = Math.Min(left.Length, right.Length);
        var length = 0;
        while (length < limit && left[length] == right[length])
        {
            length++;
        }
        return length;
    }

    private static int CommonSuffixLength(string left, string right, int prefix)
    {
        var limit = Math.Min(left.Length, right.Length) - prefix;
        var length = 0;
        while (length < limit && left[^(length + 1)] == right[^(length + 1)])
        {
            length++;
        }
        return length;
    }
}
