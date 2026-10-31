using System.Diagnostics;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal sealed class AssistantCollector(IObserverSession session, AssistantState state, EntityId observerId,
    AssistantRegistration registration, string claudeDirectory, string codexDirectory, string cacheDirectory,
    Action<string> diagnostic, Func<string?>? currentZone = null, TimeSpan? additionalHistoryBudget = null,
    AssistantLimits? limits = null)
{
    private readonly Dictionary<string, long> _liveEnds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastVisited = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unfinished = new(StringComparer.Ordinal);
    private long _visit;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly AssistantDelivery _delivery = new(session, state, diagnostic);
    private readonly AssistantLimits _limits = limits ?? AssistantLimits.Default;

    internal async Task PollAsync(CancellationToken token = default)
    {
        await state.ResumeAsync(session, token);
        if (!await _delivery.CheckAsync(token)) return;
        var files = SourceFiles.Discover(claudeDirectory, codexDirectory, diagnostic);
        var known = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _liveEnds.Keys.Where(path => !known.Contains(path)).ToArray()) _liveEnds.Remove(removed);
        var timer = Stopwatch.StartNew();
        // Live tails get one bounded slice every poll. Round-robin historical files cannot starve each other.
        var ordered = files.OrderByDescending(IsRecent)
            .ThenByDescending(file => _liveEnds.ContainsKey(file.Path))
            .ThenBy(file => _lastVisited.GetValueOrDefault(file.Path)).ThenBy(file => file.Path, StringComparer.Ordinal).ToArray();
        foreach (var file in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (!IsRecent(file) && !_liveEnds.ContainsKey(file.Path) && timer.Elapsed > TimeSpan.FromSeconds(3)) break;
            try
            {
                await ReadFileAsync(file, token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ZstdSharp.ZstdException)
            { diagnostic(file.Path + ": " + exception.Message); }
            _lastVisited[file.Path] = ++_visit;
        }
        // Spend the remaining work budget on bounded history slices instead of idling five seconds after only 16 lines.
        var budget = additionalHistoryBudget ?? TimeSpan.FromSeconds(3);
        while (timer.Elapsed < budget)
        {
            var backlog = files.Where(file => _unfinished.Contains(file.Path)).OrderBy(file => _lastVisited.GetValueOrDefault(file.Path)).ToArray();
            if (backlog.Length == 0) break;
            foreach (var file in backlog)
            {
                if (timer.Elapsed >= budget) break;
                try { await ReadFileAsync(file, token); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ZstdSharp.ZstdException)
                { _unfinished.Remove(file.Path); diagnostic(file.Path + ": " + exception.Message); }
                _lastVisited[file.Path] = ++_visit;
            }
        }
    }

    internal async Task ReadFileAsync(SourceFile file, CancellationToken token = default)
    {
        await state.ResumeAsync(session, token);
        var information = new FileInfo(file.Path);
        var identity = FileIdentity.Read(file.Path);
        var size = information.Length;
        var modified = information.LastWriteTimeUtc.Ticks;
        var key = AssistantState.Key("file", file.Path);
        var previous = state.Read<FileProgress>(key);
        var compressed = file.Path.EndsWith(".zst", StringComparison.Ordinal);
        var reset = previous is null || previous.Identity != identity || size < previous.Size
            || size == previous.Size && modified != previous.Modified || compressed && (size != previous.Size || modified != previous.Modified);
        if (reset) _liveEnds.Remove(file.Path);
        var context = reset ? new SourceContext() : previous!.Context;
        var offset = reset ? 0 : previous!.Offset;
        var batch = StartBatch(file, context);
        // Completed archives need no decompression on later polls; identity/size/mtime still detect replacement.
        if (compressed && !reset && previous!.Offset == -1)
        { _unfinished.Remove(file.Path); await state.SubmitAsync(session, batch.Payload, batch.Changes, token); return; }
        if (!compressed && !reset && offset == size && modified == previous!.Modified && batch.OperationCount == 0)
        { _unfinished.Remove(file.Path); _liveEnds[file.Path] = offset; return; }
        var expanded = await SourceFiles.ExpandAsync(file.Path, identity + ":" + size + ":" + modified, cacheDirectory, token);
        var zone = !reset && _liveEnds.TryGetValue(file.Path, out var liveOffset) && offset >= liveOffset
            ? (currentZone?.Invoke() ?? LocalZone()) : null;
        var length = new FileInfo(expanded).Length;
        // A segment that exceeds the Runtime's batch limits is read again with half as many lines.
        // One line that still does not fit becomes a diagnostic, and the checkpoint moves past it.
        IReadOnlyList<SourceLine> lines;
        bool atEnd;
        for (var maximumLines = 16; ; )
        {
            lines = await SourceFiles.ReadAsync(expanded, offset, maximumLines, token, _limits.LineBytes);
            var trial = StartBatch(file, context);
            var notes = new List<string>();
            var (nextContext, nextOffset) = Interpret(file, lines, context, offset, zone, trial, notes);
            atEnd = nextOffset == length;
            trial.Set(key, new FileProgress(identity, size, modified, compressed && atEnd ? -1 : nextOffset, nextContext));
            if (AssistantState.Fits(trial.Payload, _limits))
            {
                notes.ForEach(diagnostic);
                (batch, context, offset) = (trial, nextContext, nextOffset);
                break;
            }
            if (lines.Count > 1) { maximumLines = Math.Max(1, lines.Count / 2); continue; }
            diagnostic($"{file.Path}@{offset}: the interaction exceeds the Runtime's batch limits and was skipped.");
            offset = lines[0].End;
            atEnd = offset == length;
            batch.Set(key, new FileProgress(identity, size, modified, compressed && atEnd ? -1 : offset, context));
            break;
        }
        await state.SubmitAsync(session, batch.Payload, batch.Changes, token);
        if (!atEnd && lines.Count > 0) _unfinished.Add(file.Path);
        else _unfinished.Remove(file.Path);
        if (atEnd)
        {
            _liveEnds[file.Path] = offset;
            if (compressed) File.Delete(expanded);
        }
    }

    private (SourceContext Context, long Offset) Interpret(SourceFile file, IReadOnlyList<SourceLine> lines,
        SourceContext context, long offset, string? zone, AssistantBatch batch, List<string> notes)
    {
        foreach (var line in lines)
        {
            if (batch.OperationCount > 400) break;
            if (line.Oversized)
            {
                notes.Add($"{file.Path}@{offset}: the line exceeds {_limits.LineBytes} bytes and was skipped.");
                offset = line.End;
                continue;
            }
            // Parsing works on a copy: malformed data must not partially update the context checkpoint.
            var next = JsonSerializer.Deserialize<SourceContext>(JsonSerializer.Serialize(context))!;
            try
            {
                if (line.Bytes.Length > 0)
                {
                    if (file.IsIndex)
                    {
                        using var index = JsonDocument.Parse(line.Bytes);
                        batch.Session(new("codex", index.RootElement.Required("id"), index.RootElement.Required("thread_name")));
                    }
                    else
                    {
                        var interactions = file.Product == "codex" ? CodexSource.Read(line.Bytes, next, zone, ReadImage)
                            : ClaudeSource.Read(line.Bytes, next, file.Location!, zone);
                        if (next.Session is { } info)
                        {
                            batch.Session(info);
                            foreach (var interaction in interactions) batch.Add(info, interaction);
                        }
                    }
                }
                context = next;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
            { notes.Add($"{file.Path}@{offset}: {exception.Message}"); }
            offset = line.End;
        }
        return (context, offset);
    }

    private AssistantBatch StartBatch(SourceFile file, SourceContext context)
    {
        var batch = new AssistantBatch(state, observerId, registration);
        if (file.Location is { ParentId: not null } subagent && context.Session is { } existing)
        {
            context.Session = existing with { Title = subagent.Title ?? existing.Title, AgentName = subagent.AgentName ?? existing.AgentName,
                SpawnCallId = subagent.SpawnCallId ?? existing.SpawnCallId };
            batch.Session(context.Session);
        }
        return batch;
    }

    private bool IsRecent(SourceFile file) => File.GetLastWriteTimeUtc(file.Path) >= _started;

    private BinaryContent? ReadImage(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) { diagnostic("Relative image path has no proven base: " + path); return null; }
            var bytes = File.ReadAllBytes(path);
            var media = Path.GetExtension(path).ToLowerInvariant() switch
            { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "application/octet-stream" };
            return new(bytes, media);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostic("Image unavailable: " + path + ": " + exception.Message); return null; }
    }

    private static string? LocalZone()
    {
        var zone = TimeZoneInfo.Local;
        return zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var id) ? id : null;
    }
}
