namespace Heartbeat.Hub.Runtime;

internal static class HubIdentity
{
    public static Guid ReadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "hub-id");
        if (File.Exists(path)) return Read(path);
        var id = Guid.NewGuid();
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, id.ToString("D"));
        try { File.Move(temporary, path); }
        catch (IOException) when (File.Exists(path)) { return Read(path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return id;
    }

    private static Guid Read(string path) => Guid.TryParse(File.ReadAllText(path), out var id) && id != Guid.Empty
        ? id : throw new InvalidDataException("Hub identity is invalid. Restore its original data directory.");
}
