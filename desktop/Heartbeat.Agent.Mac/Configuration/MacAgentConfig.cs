namespace Heartbeat.Agent.Mac.Configuration;

public sealed class MacAgentConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string ThemeMode { get; set; } = "System";

    private int _uploadIntervalMinutes = 1;
    public int UploadIntervalMinutes
    {
        get => _uploadIntervalMinutes;
        set => _uploadIntervalMinutes = Math.Max(1, value);
    }

    public List<string> AwayProcessNames { get; set; } = [];
    public int IngestPort { get; set; } = 24820;
    public bool InputEventRecordingEnabled { get; set; }
    public Dictionary<string, MacCollectorEntry> Collectors { get; set; } = [];
}

public sealed class MacCollectorEntry
{
    public bool Enabled { get; set; } = true;
    public int? FlushPeriodMs { get; set; }
    public string? DeclarationJson { get; set; }
    public int? DeclarationVersion { get; set; }
}
