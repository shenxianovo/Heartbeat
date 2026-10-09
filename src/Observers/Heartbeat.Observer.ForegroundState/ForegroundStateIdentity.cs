using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Observers.ForegroundState;

internal static class ForegroundStateIdentity
{
    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "Heartbeat", "observers", "macos-system", "identity.json");

    internal static EntityId LoadOrCreate(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path)) return Read(path);
        // File.Move(overwrite: false) can race on Unix (stat followed by rename).
        // Serialize first publication across threads and collector processes.
        using var mutex = new Mutex(false, "Heartbeat.MacOS.ObserverIdentity");
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Ownership was acquired; recheck the file left by the previous process.
        }
        try
        {
            return CreateIfAbsent(path);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static EntityId CreateIfAbsent(string path)
    {
        if (File.Exists(path)) return Read(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var id = EntityId.New();
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { observerId = id.Value }));
            File.Move(temporary, path, overwrite: false);
            return id;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static EntityId Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var id = document.RootElement.GetProperty("observerId").GetGuid();
            var variant = id.ToString("D")[19];
            if (id.Version != 7 || variant is not ('8' or '9' or 'a' or 'b'))
                throw new FormatException("observerId must be a UUIDv7.");
            return new EntityId(id);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"Invalid identity file '{path}'; it was not replaced.", exception);
        }
    }
}
