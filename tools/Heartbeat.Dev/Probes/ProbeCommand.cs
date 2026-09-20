using System.CommandLine;
using System.CommandLine.Help;
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

    public Command CreateCommand()
    {
        var group = new Command("probe", "Measure real host behaviour before choosing a rule parameter");
        group.SetAction(parse => new HelpAction().Invoke(parse));
        var command = new Command("window-title", "Observe foreground window title churn, or analyse saved readings");
        var duration = new Option<double>("--duration-seconds") { DefaultValueFactory = _ => 120, Description = "Observation duration in seconds (at least 10)" };
        var poll = new Option<double>("--poll-milliseconds") { DefaultValueFactory = _ => 250, Description = "Polling interval in milliseconds (at least 50)" };
        var sensitive = new Option<bool>("--include-sensitive-evidence") { Description = "Persist raw window titles, which contain user context" };
        var database = new Option<bool>("--from-database") { Description = "Export projected Records from the local database" };
        var since = Timestamp("--since", "Start of the exported window; local time unless offset is given");
        var until = Timestamp("--until", "End of the exported window (default: now)");
        var readings = new Option<FileInfo>("--readings") { Description = "Analyse a previously captured readings file" };
        readings.AcceptExistingOnly();
        var dwells = new Option<double[]?>("--dwell-seconds")
        {
            Description = "Comma-separated candidate dwell seconds (default: 0.25,0.5,1,2,5)",
            Arity = ArgumentArity.ExactlyOne,
            CustomParser = result =>
            {
                var values = result.Tokens.Single().Value.Split(',', StringSplitOptions.TrimEntries);
                var parsed = new List<double>();
                foreach (var value in values)
                {
                    if (!double.TryParse(value, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number <= 0)
                    {
                        result.AddError("--dwell-seconds requires positive finite numbers separated by commas.");
                        return null;
                    }
                    parsed.Add(number);
                }
                return parsed.ToArray();
            },
        };
        command.Options.Add(duration);
        command.Options.Add(poll);
        command.Options.Add(sensitive);
        command.Options.Add(database);
        command.Options.Add(since);
        command.Options.Add(until);
        command.Options.Add(readings);
        command.Options.Add(dwells);
        command.SetAction((parse, token) => RunAsync(ProbeOptions.Create(
            Duration(parse.GetValue(duration), milliseconds: false), Duration(parse.GetValue(poll), milliseconds: true),
            parse.GetValue(sensitive), parse.GetValue(readings)?.FullName, parse.GetValue(database),
            parse.GetValue(since), parse.GetValue(until), parse.GetValue(dwells)), token));
        group.Subcommands.Add(command);
        return group;
    }

    private static TimeSpan Duration(double value, bool milliseconds)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new CommandUsageException("Probe durations must be positive finite numbers.");
        try
        {
            return milliseconds ? TimeSpan.FromMilliseconds(value) : TimeSpan.FromSeconds(value);
        }
        catch (OverflowException)
        {
            throw new CommandUsageException("Probe durations must fit within TimeSpan range.");
        }
    }

    private static Option<DateTimeOffset?> Timestamp(string name, string description) => new(name)
    {
        Description = description,
        CustomParser = result =>
        {
            if (DateTimeOffset.TryParse(result.Tokens.Single().Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value))
                return value;
            result.AddError($"{name} needs a timestamp such as 2026-09-16T04:33Z.");
            return null;
        },
    };

    public Task<int> RunAsync(ProbeOptions options, CancellationToken cancellationToken) =>
        RunWindowTitleAsync(options, cancellationToken);

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
            options.IncludeSensitiveEvidence,
            output);
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
    public static ProbeOptions Create(
        TimeSpan? duration = null, TimeSpan? poll = null, bool sensitive = false, string? readings = null,
        bool database = false, DateTimeOffset? since = null, DateTimeOffset? until = null, IReadOnlyList<double>? dwells = null)
    {
        var observationDuration = duration ?? TimeSpan.FromSeconds(120);
        var pollInterval = poll ?? TimeSpan.FromMilliseconds(250);
        ValidateObservation(observationDuration, pollInterval);
        return new ProbeOptions("window-title", observationDuration, pollInterval, sensitive, readings,
            Window(database, since, until, readings), dwells);
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

}
