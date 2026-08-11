using Heartbeat.Desktop.Core.Configuration;

namespace Heartbeat.Agent.Configuration;

/// <summary>把 Windows host 的持久化配置投影为 Desktop.Core 所需的最小设置接口。</summary>
public sealed class DesktopSettingsAdapter : IDesktopSettings, IDisposable
{
    private readonly ConfigManager _configManager;

    public DesktopSettingsAdapter(ConfigManager configManager)
    {
        _configManager = configManager;
        _configManager.ConfigChanged += OnConfigChanged;
    }

    public IReadOnlyList<string> AwayProcessNames => _configManager.Current.AwayProcessNames;
    public bool SplitFocusedWindowChangesUnconditionally => false;

    public event Action<IReadOnlyList<string>>? AwayProcessNamesChanged;

    private void OnConfigChanged(Models.AgentConfig config)
        => AwayProcessNamesChanged?.Invoke(config.AwayProcessNames);

    public void Dispose()
    {
        _configManager.ConfigChanged -= OnConfigChanged;
        GC.SuppressFinalize(this);
    }
}
