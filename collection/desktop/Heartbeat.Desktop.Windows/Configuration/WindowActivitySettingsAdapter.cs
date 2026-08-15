using Heartbeat.Desktop.Windows.Models;

namespace Heartbeat.Desktop.Windows.Configuration;

public interface IWindowActivityCollectionPolicy
{
    bool Enabled { get; }
    event Action<bool>? Changed;
}

public sealed class WindowActivitySettingsAdapter : IWindowActivityCollectionPolicy, IDisposable
{
    private readonly ConfigManager _config;
    private bool _enabled;

    public WindowActivitySettingsAdapter(ConfigManager config)
    {
        _config = config;
        _enabled = config.Current.WindowActivityCollectionEnabled;
        _config.ConfigChanged += OnConfigChanged;
    }

    public bool Enabled => Volatile.Read(ref _enabled);
    public event Action<bool>? Changed;

    private void OnConfigChanged(AgentConfig config)
    {
        var previous = _enabled;
        Volatile.Write(ref _enabled, config.WindowActivityCollectionEnabled);
        if (previous != config.WindowActivityCollectionEnabled)
            Changed?.Invoke(config.WindowActivityCollectionEnabled);
    }

    public void Dispose()
    {
        _config.ConfigChanged -= OnConfigChanged;
        GC.SuppressFinalize(this);
    }
}
