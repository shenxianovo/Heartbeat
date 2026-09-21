using System.Text.Json;

namespace Heartbeat.Hub.Runtime;

// One storage root per Hub. Runtime components use logical document names;
// directory layout, atomic file replacement and identity ownership live here.
public sealed class HubLocalStorage : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly FileStream _ownership;
    private readonly Lazy<LocalSecretStore> _secrets;

    public HubLocalStorage(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _ownership = new FileStream(Path.Combine(DirectoryPath, ".runtime.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try { Id = HubIdentity.ReadOrCreate(DirectoryPath); }
        catch { _ownership.Dispose(); throw; }
        _secrets = new(() => new LocalSecretStore(Path.Combine(DirectoryPath, "secrets")));
    }

    public string DirectoryPath { get; }
    public Guid Id { get; }
    public string DatabasePath => Path.Combine(DirectoryPath, "hub.sqlite");
    public LocalSecretStore Secrets => _secrets.Value;

    public T? ReadDocument<T>(string name) where T : class
    {
        var path = DocumentPath(name);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Local Hub document '{name}' is invalid.");
    }

    public void WriteDocument<T>(string name, T value)
    {
        var path = DocumentPath(name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private string DocumentPath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '-'))
            throw new ArgumentException("A local document name must contain only ASCII letters, digits or hyphens.", nameof(name));
        return Path.Combine(DirectoryPath, name + ".json");
    }

    public void Dispose() => _ownership.Dispose();
}
