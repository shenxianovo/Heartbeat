using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Observers.ApplicationIdentity;

// A pending batch survives a lost response or process exit: VERSION-7 retries the same versions.
internal sealed class IdentityState
{
    private readonly string directory;
    public EntityId ObserverId { get; }
    public Dictionary<string, string> Unknown { get; }

    public IdentityState(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        var identity = Path.Combine(directory, "observer-id");
        ObserverId = File.Exists(identity) ? new(Guid.Parse(File.ReadAllText(identity))) : EntityId.New();
        if (!File.Exists(identity)) Write("observer-id", ObserverId.Value.ToString());
        var unknown = Path.Combine(directory, "unknown.json");
        Unknown = File.Exists(unknown)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(unknown))! : [];
    }

    public void SaveUnknown() => Write("unknown.json", JsonSerializer.Serialize(Unknown));
    public string? Pending => File.Exists(Path.Combine(directory, "pending.json"))
        ? File.ReadAllText(Path.Combine(directory, "pending.json")) : null;
    public void SavePending(string body) => Write("pending.json", body);
    public void ClearPending() => File.Delete(Path.Combine(directory, "pending.json"));

    private void Write(string name, string value)
    {
        var path = Path.Combine(directory, name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, value);
        File.Move(temporary, path, true);
    }
}
