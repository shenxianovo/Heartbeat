using Heartbeat.Collector.System.Input;
using Heartbeat.Desktop.Windows.Models;

namespace Heartbeat.Desktop.Windows.Configuration;

public sealed class InteractionSignalSettingsAdapter : IInteractionSignalPolicy, IDisposable
{
    private readonly ConfigManager _config;
    private bool _enabled;

    public InteractionSignalSettingsAdapter(ConfigManager config)
    {
        _config = config;
        _enabled = Compute(config.Current);
        _config.ConfigChanged += OnConfigChanged;
    }

    public bool Enabled => Volatile.Read(ref _enabled);
    public event Action<bool>? Changed;

    private void OnConfigChanged(AgentConfig config)
    {
        var enabled = Compute(config);
        var previous = _enabled;
        Volatile.Write(ref _enabled, enabled);
        if (previous != enabled)
            Changed?.Invoke(enabled);
    }

    private static bool Compute(AgentConfig config) =>
        config.InteractionSignalEnabled && config.WindowActivityCollectionEnabled;

    public void Dispose()
    {
        _config.ConfigChanged -= OnConfigChanged;
        GC.SuppressFinalize(this);
    }
}
