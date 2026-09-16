using System.Globalization;
using System.Text.Json;

namespace Heartbeat.Dev;

/// 现场探针：在宿主机上采一段真实读数，用来回答「规则参数该取多少」。
/// 探针不写数据库、不连 Hub、不起容器：要量的是原生读数流本身，
/// 数据库里存的是投影之后的 Record，已经被当前切分规则改写过，不能用来评价切分规则。
internal sealed class ProbeCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    private const string ReadingsName = "window-title-readings.json";
    private const string ReportName = "window-title-churn.json";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await output.WriteLineAsync("""
                Usage: heartbeat-dev probe window-title [options]

                window-title  Record foreground window title churn on this Mac and report what it would cost in records

                Options:
                  --duration-seconds N          How long to observe (default 120)
                  --poll-milliseconds N         Polling cadence beside native notifications (default 250)
                  --readings PATH               Analyse readings captured earlier instead of observing now
                  --from-database               Export records from the local database instead of observing now
                  --since TIMESTAMP             Start of the exported window (local time unless an offset is given)
                  --until TIMESTAMP             End of the exported window (default now)
                  --dwell-seconds A,B,C         Candidate dwell times to simulate (default 0.25,0.5,1,2,5)
                  --include-sensitive-evidence  Persist raw window titles, which contain user context
                """);
            return 0;
        }

        var options = ProbeOptions.Parse(args);
        return options.Name switch
        {
            "window-title" => await RunWindowTitleAsync(options, cancellationToken),
            _ => throw new CommandUsageException($"Unknown probe '{options.Name}'."),
        };
    }

    private async Task<int> RunWindowTitleAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        if (options.Readings is null && options.Database is null && !OperatingSystem.IsMacOS())
        {
            throw new CommandUsageException("The window-title probe reads macOS foreground state and requires macOS.");
        }

        List<string> limitations =
        [
            options.Database is null
                ? "Readings come from one session on one Mac; parameters chosen from a single probe do not generalise."
                : "Readings come from one Mac's own records; parameters chosen from one machine do not generalise.",
            "Titles are recorded as fingerprints and shape metrics unless --include-sensitive-evidence is set.",
        ];
        if (options.Database is not null)
        {
            limitations.Add(
                "Database readings are projected records, not the native reading stream:"
                + " they cannot show whether a native notification was missed.");
        }

        return await EvidenceSession.ExecuteAsync(repository, "probe", "window-title",
            limitations,
            evidence => ObserveAsync(options, evidence, cancellationToken),
            options.IncludeSensitiveEvidence);
    }

    private async Task<int> ObserveAsync(
        ProbeOptions options,
        EvidenceSession evidence,
        CancellationToken cancellationToken)
    {
        var run = evidence.Run;
        if (options.Database is { } window)
        {
            // 数据库里是好几天的 Record，比一次会话更能代表日常抖动，代价是它已经被当前切分规则改写过。
            var exported = await new DatabaseReadings(repository, runner)
                .ExportAsync(window, options.IncludeSensitiveEvidence, evidence.Commands, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(run.Directory, ReadingsName),
                JsonSerializer.Serialize(exported, JsonOptions.Indented),
                cancellationToken);
            await output.WriteLineAsync(
                $"Exported {exported.Readings.Count} readings from records between {window.Since:u} and {window.Until:u}.");
            return await ReportAsync(run, options.Dwells, cancellationToken);
        }

        if (options.Readings is not null)
        {
            // Analysing readings captured earlier keeps the same report next to its own evidence.
            File.Copy(options.Readings, Path.Combine(run.Directory, ReadingsName));
            evidence.Commands.Add($"analyse {Path.GetFileName(options.Readings)}");
            return await ReportAsync(run, options.Dwells, cancellationToken);
        }

        var assembly = await CollectorBuild.EnsureAsync(repository, runner, run, evidence.Commands, cancellationToken);
        var arguments = ProbeArguments(options, Path.Combine(run.Directory, ReadingsName));
        evidence.Commands.Add($"dotnet {Path.GetFileName(assembly)} {string.Join(' ', arguments)}");
        await output.WriteLineAsync($"Probe evidence: {run.Directory}");
        await output.WriteLineAsync(
            $"Observing for {options.Duration.TotalSeconds:F0}s. Use this Mac as you normally would, and make sure to visit"
            + " the windows whose titles animate: media players, downloads, progress dialogs, terminals.");

        var exitCode = await RunProbeProcessAsync(assembly, arguments, run, cancellationToken);
        if (exitCode != 0)
        {
            return exitCode;
        }

        return await ReportAsync(run, options.Dwells, cancellationToken);
    }

    private static string[] ProbeArguments(ProbeOptions options, string readingsPath)
    {
        var arguments = new List<string>
        {
            "--probe-window-titles",
            "--duration-seconds", options.Duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture),
            "--poll-milliseconds", options.PollInterval.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
            "--output", readingsPath,
        };
        if (options.IncludeSensitiveEvidence)
        {
            arguments.Add("--include-titles");
        }
        return [.. arguments];
    }

    private async Task<int> RunProbeProcessAsync(
        string assembly,
        IReadOnlyList<string> arguments,
        ArtifactRun run,
        CancellationToken cancellationToken)
    {
        using var process = ProcessRunner.StartManaged(repository.Root, assembly, arguments, null);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Interrupt instead of kill: the probe still writes the readings it already has.
            await ProcessRunner.InterruptAsync(process, TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        var log = await process.StandardOutput.ReadToEndAsync(CancellationToken.None)
            + await process.StandardError.ReadToEndAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(run.Directory, "probe.log"), log, CancellationToken.None);
        if (process.ExitCode != 0)
        {
            await output.WriteLineAsync(log);
        }
        return process.ExitCode;
    }

    private async Task<int> ReportAsync(
        ArtifactRun run,
        IReadOnlyList<double>? dwells,
        CancellationToken cancellationToken)
    {
        var readingsPath = Path.Combine(run.Directory, ReadingsName);
        if (!File.Exists(readingsPath))
        {
            await output.WriteLineAsync("The probe wrote no readings; see probe.log.");
            return 1;
        }

        var probe = await ReadProbeAsync(readingsPath, cancellationToken);
        var report = WindowTitleChurn.Analyze(probe.Readings, dwells);
        await File.WriteAllTextAsync(
            Path.Combine(run.Directory, ReportName),
            JsonSerializer.Serialize(report, JsonOptions.Indented),
            cancellationToken);
        foreach (var outage in probe.Outages)
        {
            await output.WriteLineAsync($"Capability {outage}");
        }
        await output.WriteLineAsync(WindowTitleChurn.Summarize(report));
        if (WindowTitleChurn.TitleWasNeverReadable(report))
        {
            // Reporting "no title changes" here would be a lie: the probe never saw a title at all.
            await output.WriteLineAsync(
                "No window title was readable during the probe, so its churn numbers say nothing."
                + " Grant Accessibility to the terminal running heartbeat-dev"
                + " (System Settings > Privacy & Security > Accessibility) and probe again.");
            return 1;
        }
        return 0;
    }

    private static async Task<ProbeReadings> ReadProbeAsync(string path, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return new ProbeReadings(Readings(document), Outages(document));
    }

    private static IReadOnlyList<string> Outages(JsonDocument document) =>
    [
        .. document.RootElement.GetProperty("capabilities").EnumerateArray()
            .Where(capability => capability.GetProperty("state").GetString() != "Available")
            .Select(capability =>
                $"{capability.GetProperty("capability").GetString()} {capability.GetProperty("state").GetString()}"
                + $" ({capability.GetProperty("reason").GetString() ?? "no reason"})")
            .Distinct(StringComparer.Ordinal)
    ];

    private static IReadOnlyList<WindowTitleReading> Readings(JsonDocument document) =>
        [
            .. document.RootElement.GetProperty("readings").EnumerateArray().Select(reading => new WindowTitleReading(
                reading.GetProperty("at").GetDateTimeOffset(),
                reading.GetProperty("origin").GetString() ?? "unknown",
                reading.GetProperty("application").GetString(),
                reading.GetProperty("titleHash").GetString(),
                reading.GetProperty("titleLength").GetInt32(),
                reading.GetProperty("rotationOfPrevious").GetBoolean()))
        ];

    private sealed record ProbeReadings(IReadOnlyList<WindowTitleReading> Readings, IReadOnlyList<string> Outages);
}

internal sealed record ProbeOptions(
    string Name,
    TimeSpan Duration,
    TimeSpan PollInterval,
    bool IncludeSensitiveEvidence,
    string? Readings,
    DatabaseWindow? Database,
    IReadOnlyList<double>? Dwells)
{
    public static ProbeOptions Parse(IReadOnlyList<string> args)
    {
        var duration = TimeSpan.FromSeconds(120);
        var poll = TimeSpan.FromMilliseconds(250);
        var sensitive = false;
        var database = false;
        DateTimeOffset? since = null;
        DateTimeOffset? until = null;
        string? readings = null;
        IReadOnlyList<double>? dwells = null;
        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--include-sensitive-evidence": sensitive = true; break;
                case "--from-database": database = true; break;
                case "--since": since = Moment(args, ++index); break;
                case "--until": until = Moment(args, ++index); break;
                case "--readings": readings = Text(args, ++index); break;
                case "--dwell-seconds": dwells = ParseDwells(args, ++index); break;
                case "--duration-seconds": duration = TimeSpan.FromSeconds(Number(args, ++index)); break;
                case "--poll-milliseconds": poll = TimeSpan.FromMilliseconds(Number(args, ++index)); break;
                default: throw new CommandUsageException($"Unknown probe option '{args[index]}'.");
            }
        }

        ValidateObservation(duration, poll);
        return new ProbeOptions(
            args[0], duration, poll, sensitive, readings, Window(database, since, until, readings), dwells);
    }

    private static void ValidateObservation(TimeSpan duration, TimeSpan poll)
    {
        if (duration < TimeSpan.FromSeconds(10))
        {
            throw new CommandUsageException("A probe shorter than 10 seconds cannot say anything about churn.");
        }
        if (poll < TimeSpan.FromMilliseconds(50) || poll > duration)
        {
            throw new CommandUsageException("The poll interval must be at least 50 ms and shorter than the duration.");
        }
    }

    /// 读数只能有一个来源：现场观测、已有读数文件，或者数据库导出。
    private static DatabaseWindow? Window(
        bool database,
        DateTimeOffset? since,
        DateTimeOffset? until,
        string? readings)
    {
        if (!database)
        {
            return since is null && until is null
                ? null
                : throw new CommandUsageException("'--since' and '--until' only apply to '--from-database'.");
        }
        if (readings is not null)
        {
            throw new CommandUsageException("Choose one source: '--from-database' or '--readings'.");
        }
        if (since is null)
        {
            throw new CommandUsageException("'--from-database' needs '--since' to bound the export.");
        }

        var end = until ?? DateTimeOffset.UtcNow;
        return since < end
            ? new DatabaseWindow(since.Value, end)
            : throw new CommandUsageException("'--since' must come before '--until'.");
    }

    private static DateTimeOffset Moment(IReadOnlyList<string> args, int index)
    {
        var text = Text(args, index, requireFile: false);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var moment)
            ? moment
            : throw new CommandUsageException(
                $"'{args[index - 1]}' needs a timestamp such as 2026-09-16T12:33 or 2026-09-16T04:33Z.");
    }

    private static double[] ParseDwells(IReadOnlyList<string> args, int index)
    {
        var dwells = Text(args, index, requireFile: false)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.TryParse(value, CultureInfo.InvariantCulture, out var dwell) && dwell > 0
                ? dwell
                : throw new CommandUsageException($"'{value}' is not a positive number of seconds."))
            .ToArray();
        return dwells.Length > 0
            ? dwells
            : throw new CommandUsageException("'--dwell-seconds' needs at least one dwell time.");
    }

    private static string Text(IReadOnlyList<string> args, int index, bool requireFile = true)
    {
        if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new CommandUsageException($"'{args[index - 1]}' needs a value.");
        }
        if (!requireFile)
        {
            return args[index];
        }
        var path = Path.GetFullPath(args[index]);
        return File.Exists(path) ? path : throw new CommandUsageException($"No readings file at {path}.");
    }

    private static double Number(IReadOnlyList<string> args, int index)
    {
        if (index >= args.Count
            || !double.TryParse(args[index], CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new CommandUsageException($"'{args[index - 1]}' needs a positive number.");
        }
        return value;
    }
}
