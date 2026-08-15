using System.Text.Json;
using Serilog;

namespace Heartbeat.Desktop.Mac.Configuration;

/// <summary>macOS platform head 所有的原子 config.json 持久化。</summary>
public sealed class MacConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private MacAgentConfig _current;

    public MacConfigManager(MacAgentPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "config.json");
        _current = LoadOrCreate();
    }

    public event Action<MacAgentConfig>? ConfigChanged;

    public MacAgentConfig Current
    {
        get
        {
            lock (_gate)
                return Clone(_current);
        }
    }

    public void Update(Action<MacAgentConfig> modifier)
    {
        MacAgentConfig snapshot;
        lock (_gate)
        {
            modifier(_current);
            Normalize(_current);
            Save(_current);
            snapshot = Clone(_current);
        }
        ConfigChanged?.Invoke(snapshot);
    }

    private MacAgentConfig LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<MacAgentConfig>(File.ReadAllText(_path), JsonOptions);
                if (loaded != null)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "读取 macOS 配置失败，将使用默认配置: {Path}", _path);
        }

        var created = new MacAgentConfig();
        Save(created);
        return created;
    }

    private void Save(MacAgentConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }

    private static void Normalize(MacAgentConfig config)
    {
        config.AwayProcessNames ??= [];
        config.Collectors ??= [];
        config.ThemeMode = NormalizeThemeMode(config.ThemeMode);
    }

    private static string NormalizeThemeMode(string? value) => value?.ToLowerInvariant() switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System"
    };

    private static MacAgentConfig Clone(MacAgentConfig source) => new()
    {
        ApiKey = source.ApiKey,
        DeviceName = source.DeviceName,
        ThemeMode = source.ThemeMode,
        UploadIntervalMinutes = source.UploadIntervalMinutes,
        AwayProcessNames = [.. source.AwayProcessNames],
        IngestPort = source.IngestPort,
        WindowTitleObservationEnabled = source.WindowTitleObservationEnabled,
        InteractionSignalEnabled = source.InteractionSignalEnabled,
        InputEventRecordingEnabled = source.InputEventRecordingEnabled,
        Collectors = source.Collectors.ToDictionary(
            pair => pair.Key,
            pair => new MacCollectorEntry
            {
                Enabled = pair.Value.Enabled,
                FlushPeriodMs = pair.Value.FlushPeriodMs,
                DeclarationJson = pair.Value.DeclarationJson,
                DeclarationVersion = pair.Value.DeclarationVersion,
            },
            StringComparer.OrdinalIgnoreCase),
    };
}

public sealed record MacAgentPaths(string DataDirectory)
{
    public static MacAgentPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Heartbeat"));
}
