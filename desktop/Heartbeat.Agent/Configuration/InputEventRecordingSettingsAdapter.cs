using Heartbeat.Agent.Models;
using Heartbeat.Hub.Core.Configuration;

namespace Heartbeat.Agent.Configuration;

public sealed class InputEventRecordingSettingsAdapter : IInputEventRecordingPolicy, IDisposable
{
    private readonly ConfigManager _configManager;
    private bool _enabled;

    public InputEventRecordingSettingsAdapter(ConfigManager configManager)
    {
        _configManager = configManager;
        _enabled = configManager.Current.InputEventRecordingEnabled;
        configManager.ConfigChanged += OnConfigChanged;
    }

    public bool Enabled => Volatile.Read(ref _enabled);
    public event Action<bool>? Changed;

    private void OnConfigChanged(AgentConfig config)
    {
        var previous = _enabled;
        Volatile.Write(ref _enabled, config.InputEventRecordingEnabled);
        if (previous != config.InputEventRecordingEnabled)
            Changed?.Invoke(config.InputEventRecordingEnabled);
    }

    public void Dispose()
    {
        _configManager.ConfigChanged -= OnConfigChanged;
        GC.SuppressFinalize(this);
    }
}
