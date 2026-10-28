using System.Text.Json;
using System.Collections.Concurrent;
using Orisumi.Core;

namespace Orisumi.Observers.ForegroundState;

internal sealed class LocalApplicationRegistry(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly ConcurrentDictionary<(int, DateTimeOffset, string?), LocalApplication> _runningApplications = [];

    internal LocalApplication Resolve(ForegroundApplicationReading reading)
    {
        var identifiers = new List<ApplicationIdentifier>();
        if (!string.IsNullOrEmpty(reading.BundleIdentifier))
            identifiers.Add(new("macos.bundle-id", reading.BundleIdentifier));
        if (!string.IsNullOrEmpty(reading.ExecutablePath))
            identifiers.Add(new("macos.executable-path", reading.ExecutablePath));

        if (string.IsNullOrEmpty(reading.ExecutablePath))
            return ResolveRunningApplication(reading, identifiers);

        // All registry read-modify-write operations stay on this thread while the mutex is held.
        using var mutex = new Mutex(false, "Orisumi.LocalApplicationRegistry");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* Re-read the last atomically published file. */ }
        try
        {
            var entries = Read();
            var existing = entries.Find(entry => entry.BundleIdentifier == reading.BundleIdentifier
                && entry.ExecutablePath == reading.ExecutablePath);
            if (existing is not null) return new(new EntityId(existing.Id), identifiers);
            var id = EntityId.New();
            entries.Add(new(id.Value, reading.BundleIdentifier, reading.ExecutablePath));
            Save(entries);
            return new(id, identifiers);
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal LocalApplication ReplaceDeleted(LocalApplication deleted)
    {
        using var mutex = new Mutex(false, "Orisumi.LocalApplicationRegistry");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            var entries = Read();
            var index = entries.FindIndex(entry => entry.Id == deleted.Id.Value);
            EntityId replacement;
            if (index >= 0)
            {
                replacement = EntityId.New();
                entries[index] = entries[index] with { Id = replacement.Value };
                Save(entries);
            }
            else
            {
                var bundle = deleted.Identifiers.FirstOrDefault(item => item.Scheme == "macos.bundle-id")?.Value;
                var executable = deleted.Identifiers.FirstOrDefault(item => item.Scheme == "macos.executable-path")?.Value;
                var existing = entries.Find(entry => entry.ExecutablePath == executable && entry.BundleIdentifier == bundle);
                replacement = existing is null ? EntityId.New() : new(existing.Id);
            }
            foreach (var entry in _runningApplications.Where(entry => entry.Value.Id == deleted.Id).ToArray())
                _runningApplications[entry.Key] = new(replacement, deleted.Identifiers);
            return new(replacement, deleted.Identifiers);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private LocalApplication ResolveRunningApplication(
        ForegroundApplicationReading reading, IReadOnlyList<ApplicationIdentifier> identifiers)
    {
        if (reading.ProcessId is not > 0 || reading.ProcessStartedAt is null)
            return new(EntityId.New(), identifiers);
        var key = (reading.ProcessId.Value, reading.ProcessStartedAt.Value, reading.BundleIdentifier);
        if (!_runningApplications.TryGetValue(key, out var application))
        {
            application = new(EntityId.New(), identifiers);
            _runningApplications.TryAdd(key, application);
        }
        return application;
    }

    private List<Entry> Read()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), JsonSerializerOptions.Web)
                ?? throw new JsonException("Expected application entries.");
            var ids = new HashSet<Guid>();
            var keys = new HashSet<(string?, string)>();
            foreach (var entry in entries)
            {
                if (entry is null || entry.Id.Version != 7
                    || entry.Id.ToString("D")[19] is not ('8' or '9' or 'a' or 'b')
                    || string.IsNullOrEmpty(entry.ExecutablePath)
                    || !ids.Add(entry.Id) || !keys.Add((entry.BundleIdentifier, entry.ExecutablePath)))
                    throw new JsonException("Invalid or duplicate application entry.");
            }
            return entries;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid application registry '{_path}'; it was not replaced.", exception);
        }
    }

    private void Save(List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entries, JsonSerializerOptions.Web);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private sealed record Entry(Guid Id, string? BundleIdentifier, string ExecutablePath);
}
