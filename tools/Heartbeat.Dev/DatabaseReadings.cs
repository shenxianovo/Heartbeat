using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Dev;

/// 数据库导出的时间窗，两端都是绝对时刻。
internal sealed record DatabaseWindow(DateTimeOffset Since, DateTimeOffset Until);

/// 数据库里的一条前台窗口 Record，结束时间已按导出窗口裁剪过。
internal sealed record WindowRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? Application,
    string? Title);

/// 与探针产物同构的读数文件，好让数据库导出与真探针共用同一套分析和报告。
internal sealed record DatabaseReadingsDocument(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Source,
    bool IncludesTitles,
    IReadOnlyList<DatabaseReading> Readings,
    IReadOnlyList<string> Capabilities);

internal sealed record DatabaseReading(
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

/// 把本地数据库里的前台窗口 Record 导成探针读数格式。
/// 这是投影之后的 Record，不是原生读数流：它答不了「原生通知有没有漏送」，那要靠真探针；
/// 它能答的是「按当前规则切出来的标题区间有多碎」，而且覆盖的是好几天，不是一次会话。
internal sealed class DatabaseReadings(RepositoryContext repository, IProcessRunner runner)
{
    // 相邻 Record 的首尾差在毫秒级以下时属于同一次交接，不是观测中断。
    private static readonly TimeSpan Handover = TimeSpan.FromMilliseconds(250);

    public async Task<DatabaseReadingsDocument> ExportAsync(
        DatabaseWindow window,
        bool includeTitles,
        List<string> commands,
        CancellationToken cancellationToken)
    {
        var envFile = repository.Path(".env.local");
        if (!File.Exists(envFile))
        {
            throw new CommandUsageException(
                $"No {envFile}, so the local database cannot be addressed. Run ./scripts/setup.sh first.");
        }

        var compose = ComposeInvocation.Create(repository, envFile, release: false);
        commands.Add($"docker compose exec db psql (foreground window records {window.Since:O} .. {window.Until:O})");
        var result = await runner.CaptureAsync(
            "docker",
            [
                .. compose, "exec", "--no-TTY", "db",
                "psql", "-U", "heartbeat", "-d", "heartbeat",
                "--no-psqlrc", "--tuples-only", "--no-align", "--command", Query(window),
            ],
            null,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new CommandUsageException(
                "Reading the local database failed. Start it with ./scripts/heartbeat-dev env up db, then export again."
                + Environment.NewLine + result.StdErr.Trim());
        }

        return ToDocument(Parse(result.StdOut), includeTitles);
    }

    /// 两种数据形态都认：新形态标题在 desktop.window.foreground，应用身份从同一时刻的应用 Record 反查；
    /// 旧形态标题在 desktop.application.foreground 的 value.window.title 里（拆 Track 之前的构建）。
    internal static string Query(DatabaseWindow window)
    {
        var since = Timestamp(window.Since);
        var until = Timestamp(window.Until);
        var windows = $"""
            select w.started_at,
                   least(coalesce(w.ended_at, now()), {until}) as ended_at,
                   (select a.value->'application'->>'id'
                    from records a join tracks ta on ta.id = a.track_id
                    where ta.type = 'desktop.application.foreground'
                      and a.started_at <= w.started_at
                      and coalesce(a.ended_at, now()) >= w.started_at
                    order by a.started_at desc limit 1) as application,
                   w.value->'window'->>'title' as title
            from records w join tracks tw on tw.id = w.track_id
            where tw.type = 'desktop.window.foreground'
              and w.started_at >= {since} and w.started_at < {until}
            """;
        var legacy = $"""
            select r.started_at,
                   least(coalesce(r.ended_at, now()), {until}) as ended_at,
                   r.value->'application'->>'id' as application,
                   r.value->'window'->>'title' as title
            from records r join tracks t on t.id = r.track_id
            where t.type = 'desktop.application.foreground' and r.value ? 'window'
              and r.started_at >= {since} and r.started_at < {until}
            """;
        return $"""
            select coalesce(json_agg(json_build_object(
                       'startedAt', to_char(s.started_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                       'endedAt', to_char(s.ended_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                       'application', s.application,
                       'title', s.title)
                       order by s.started_at, s.ended_at), '[]'::json)
            from ({windows} union all {legacy}) s;
            """;
    }

    internal static WindowRecord[] Parse(string payload)
    {
        var json = payload.Trim();
        return json.Length == 0
            ? []
            : JsonSerializer.Deserialize<WindowRecord[]>(json, JsonOptions.Indented) ?? [];
    }

    internal static DatabaseReadingsDocument ToDocument(IReadOnlyList<WindowRecord> records, bool includeTitles)
    {
        if (records.Count == 0)
        {
            throw new CommandUsageException(
                "No foreground window records in that window. Widen it, or check that the Collector was running then.");
        }

        var readings = new List<DatabaseReading>();
        WindowRecord? previous = null;
        foreach (var record in records.OrderBy(record => record.StartedAt).ThenBy(record => record.EndedAt))
        {
            if (previous is { } last && record.StartedAt - last.EndedAt > Handover)
            {
                // 首尾之间真的空了一段：那段时间没有标题，别让分析器把它当成一直没变。
                readings.Add(Reading(last.EndedAt, null, null, last, includeTitles));
                previous = last with { Application = null, Title = null };
            }
            readings.Add(Reading(record.StartedAt, record.Application, record.Title, previous, includeTitles));
            previous = record;
        }

        // 最后一条 Record 的结束时间也是一次读数，否则它停留了多久无从得知。
        var final = readings[^1];
        readings.Add(final with
        {
            At = previous!.EndedAt,
            SameApplicationAsPrevious = true,
            SameTitleAsPrevious = true,
            CommonPrefix = final.TitleLength,
            CommonSuffix = final.TitleLength,
            RotationOfPrevious = false,
        });
        return new DatabaseReadingsDocument(
            readings[0].At, readings[^1].At, "database", includeTitles, readings, []);
    }

    private static DatabaseReading Reading(
        DateTimeOffset at,
        string? application,
        string? title,
        WindowRecord? previous,
        bool includeTitles)
    {
        var (prefix, suffix, rotation) = Shape(previous?.Title, title);
        return new DatabaseReading(
            at,
            "record",
            application,
            null,
            title is not null,
            title?.Length ?? 0,
            Fingerprint(title),
            includeTitles ? title : null,
            application is not null && application == previous?.Application,
            title == previous?.Title,
            prefix,
            suffix,
            rotation);
    }

    private static (int Prefix, int Suffix, bool Rotation) Shape(string? previous, string? current)
    {
        if (previous is null || current is null)
        {
            return (0, 0, false);
        }
        var shortest = Math.Min(previous.Length, current.Length);
        var prefix = previous.Zip(current).TakeWhile(pair => pair.First == pair.Second).Count();
        var suffix = Math.Min(
            previous.Reverse().Zip(current.Reverse()).TakeWhile(pair => pair.First == pair.Second).Count(),
            shortest - prefix);
        var rotation = previous.Length == current.Length
            && !string.Equals(previous, current, StringComparison.Ordinal)
            && (previous + previous).Contains(current, StringComparison.Ordinal);
        return (prefix, suffix, rotation);
    }

    private static string? Fingerprint(string? title) => title is null
        ? null
        : System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title)))[..16];

    private static string Timestamp(DateTimeOffset value) =>
        $"timestamptz '{value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}+00'";
}
