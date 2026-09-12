using System.Text.Json;

namespace Heartbeat.Recording.Protocols;

public sealed class RecordProtocol
{
    private readonly Action<JsonElement> _validateValue;

    internal RecordProtocol(
        string type,
        int version,
        TimeMode timeMode,
        EndMode? endMode,
        bool supportsExtension,
        Action<JsonElement> validateValue)
    {
        Type = type;
        Version = version;
        TimeMode = timeMode;
        EndMode = endMode;
        SupportsExtension = supportsExtension;
        _validateValue = validateValue;
    }

    public string Type { get; }

    public int Version { get; }

    public TimeMode TimeMode { get; }

    public EndMode? EndMode { get; }

    public bool SupportsExtension { get; }

    public void ValidateValue(JsonElement value) => _validateValue(value);
}
