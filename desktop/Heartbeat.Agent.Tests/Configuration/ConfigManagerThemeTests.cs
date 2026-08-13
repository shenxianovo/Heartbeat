using Heartbeat.Agent.Configuration;

namespace Heartbeat.Agent.Tests.Configuration;

public sealed class ConfigManagerThemeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-config-theme-{Guid.NewGuid()}");

    [Fact]
    public void ThemeMode_IsNormalizedAndPersisted()
    {
        var path = Path.Combine(_root, "config.json");
        var config = new ConfigManager(path);

        config.Update(current => current.ThemeMode = "dark");

        Assert.Equal("Dark", config.Current.ThemeMode);
        Assert.Equal("Dark", new ConfigManager(path).Current.ThemeMode);

        config.Update(current => current.ThemeMode = "unknown");

        Assert.Equal("System", config.Current.ThemeMode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
