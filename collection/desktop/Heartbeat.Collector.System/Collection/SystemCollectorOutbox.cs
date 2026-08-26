using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collector.System.Collection;

internal sealed record SystemCollectorOutboxEntry(
    Guid MessageId,
    FactSubmission Fact);

internal sealed record SystemCollectorDeadLetterEntry(
    DateTimeOffset FailedAt,
    SystemCollectorOutboxEntry Entry,
    CollectorProtocolError Error);

internal static class SystemCollectorOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static IReadOnlyList<SystemCollectorOutboxEntry> Load(string path)
        => LoadFile<SystemCollectorOutboxEntry>(path, "outbox");

    public static string DeadLetterPath(string outboxPath) => Path.Combine(
        Path.GetDirectoryName(outboxPath)
            ?? throw new InvalidOperationException("The system Collector outbox path has no directory."),
        "system-collector-dead-letter.json");

    public static int DeadLetterCount(string outboxPath) =>
        LoadFile<SystemCollectorDeadLetterEntry>(DeadLetterPath(outboxPath), "dead letter file").Count;

    public static void Save(string path, IReadOnlyList<SystemCollectorOutboxEntry> entries) =>
        SaveFile(path, entries);

    public static int AppendDeadLetter(
        string outboxPath,
        SystemCollectorOutboxEntry entry,
        CollectorProtocolError error)
    {
        var path = DeadLetterPath(outboxPath);
        var entries = LoadFile<SystemCollectorDeadLetterEntry>(path, "dead letter file").ToList();
        entries.Add(new SystemCollectorDeadLetterEntry(DateTimeOffset.UtcNow, entry, error));
        SaveFile(path, entries);
        return entries.Count;
    }

    private static IReadOnlyList<T> LoadFile<T>(string path, string description)
    {
        if (!File.Exists(path))
            return [];
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidDataException($"The system Collector {description} cannot be null.");
    }

    private static void SaveFile<T>(string path, IReadOnlyList<T> entries)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The system Collector state path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".new";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
