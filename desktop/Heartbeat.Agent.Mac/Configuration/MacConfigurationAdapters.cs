using Heartbeat.Agent.Mac.Identity;
using Heartbeat.Desktop.Core.Configuration;
using Heartbeat.Hub.Core.Collectors;
using Heartbeat.Hub.Core.Configuration;

namespace Heartbeat.Agent.Mac.Configuration;

public sealed class MacHubConfiguration(MacConfigManager config) : IHubConfiguration, ICollectorRegistry, IDisposable
{
    public HubRuntimeSettings Current
    {
        get
        {
            var value = config.Current;
            return new HubRuntimeSettings(
                value.ApiKey,
                TimeSpan.FromMinutes(value.UploadIntervalMinutes),
                value.IngestPort);
        }
    }

    public event Action? Changed;

    public IReadOnlyDictionary<string, CollectorRegistration> Snapshot =>
        config.Current.Collectors.ToDictionary(
            pair => pair.Key,
            pair => ToRegistration(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    public CollectorRegistration Touch(string source, int? flushPeriodMs = null)
    {
        MacCollectorEntry? result = null;
        config.Update(value =>
        {
            if (!value.Collectors.TryGetValue(source, out var entry))
            {
                entry = new MacCollectorEntry();
                value.Collectors[source] = entry;
            }
            if (flushPeriodMs is > 0)
                entry.FlushPeriodMs = flushPeriodMs;
            result = entry;
        });
        return ToRegistration(result!);
    }

    public void Discover(IEnumerable<string> sources)
    {
        var missing = sources.Where(source => !config.Current.Collectors.ContainsKey(source)).ToList();
        if (missing.Count == 0) return;
        config.Update(value =>
        {
            foreach (var source in missing)
                value.Collectors.TryAdd(source, new MacCollectorEntry());
        });
    }

    public void StoreDeclaration(string source, string declarationJson, int version)
    {
        config.Update(value =>
        {
            if (!value.Collectors.TryGetValue(source, out var entry))
            {
                entry = new MacCollectorEntry();
                value.Collectors[source] = entry;
            }
            entry.DeclarationJson = declarationJson;
            entry.DeclarationVersion = version;
        });
    }

    private static CollectorRegistration ToRegistration(MacCollectorEntry entry) =>
        new(entry.Enabled, entry.FlushPeriodMs, entry.DeclarationJson, entry.DeclarationVersion);

    private void OnChanged(MacAgentConfig _) => Changed?.Invoke();

    public void Dispose()
    {
        config.ConfigChanged -= OnChanged;
        GC.SuppressFinalize(this);
    }

    public MacHubConfiguration Initialize()
    {
        config.ConfigChanged += OnChanged;
        return this;
    }
}

public sealed class MacDesktopSettings(MacConfigManager config) : IDesktopSettings, IDisposable
{
    public IReadOnlyList<string> AwayProcessNames => config.Current.AwayProcessNames;
    public bool SplitFocusedWindowChangesUnconditionally => true;
    public event Action<IReadOnlyList<string>>? AwayProcessNamesChanged;

    private void OnChanged(MacAgentConfig value) =>
        AwayProcessNamesChanged?.Invoke(value.AwayProcessNames);

    public MacDesktopSettings Initialize()
    {
        config.ConfigChanged += OnChanged;
        return this;
    }

    public void Dispose()
    {
        config.ConfigChanged -= OnChanged;
        GC.SuppressFinalize(this);
    }
}

public sealed class MacInputEventRecordingPolicy : IInputEventRecordingPolicy
{
    public bool Enabled => false;
    public event Action<bool>? Changed { add { } remove { } }
}

public sealed class MacDeviceIdentity(
    MacConfigManager config,
    MacMachineIdentity machineIdentity) : IDeviceIdentity
{
    public string HardwareId => machineIdentity.HardwareId;
    public string DeviceName => string.IsNullOrWhiteSpace(config.Current.DeviceName)
        ? Environment.MachineName
        : config.Current.DeviceName;
}
