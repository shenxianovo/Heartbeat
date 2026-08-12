using Heartbeat.Agent.Mac.Native;
using Heartbeat.Agent.Mac.Observations;

namespace Heartbeat.Agent.Mac.Icons;

public sealed class MacApplicationCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _bundlePaths = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(string? appIdentityKey, MacApplication? application)
    {
        if (appIdentityKey == null || application?.ExecutablePath == null) return;
        var marker = application.ExecutablePath.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return;
        var bundlePath = application.ExecutablePath[..(marker + ".app".Length)];
        lock (_gate) _bundlePaths[appIdentityKey] = bundlePath;
    }

    public bool TryGetBundlePath(string appIdentityKey, out string bundlePath)
    {
        lock (_gate) return _bundlePaths.TryGetValue(appIdentityKey, out bundlePath!);
    }
}

public interface IMacIconTools
{
    string? ReadBundleIconName(string infoPlistPath);
    byte[]? ConvertToPng(string iconPath);
}

public sealed class MacBundleIconExtractor(
    MacApplicationCatalog catalog,
    IMacIconTools tools)
{
    public byte[]? Extract(string appIdentityKey)
    {
        if (!catalog.TryGetBundlePath(appIdentityKey, out var bundlePath))
            return null;

        var iconName = tools.ReadBundleIconName(Path.Combine(bundlePath, "Contents", "Info.plist"));
        if (string.IsNullOrWhiteSpace(iconName))
            return null;
        if (!Path.HasExtension(iconName))
            iconName += ".icns";
        return tools.ConvertToPng(Path.Combine(bundlePath, "Contents", "Resources", iconName));
    }
}

public sealed class MacIconTools(IMacCommandRunner commands) : IMacIconTools
{
    public string? ReadBundleIconName(string infoPlistPath)
    {
        if (!File.Exists(infoPlistPath)) return null;
        var result = commands.Run("/usr/bin/plutil", ["-extract", "CFBundleIconFile", "raw", "-o", "-", infoPlistPath]);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public byte[]? ConvertToPng(string iconPath)
    {
        if (!File.Exists(iconPath)) return null;
        var outputPath = Path.Combine(Path.GetTempPath(), $"heartbeat-icon-{Guid.NewGuid()}.png");
        try
        {
            var result = commands.Run("/usr/bin/sips", ["-s", "format", "png", iconPath, "--out", outputPath]);
            return result.ExitCode == 0 && File.Exists(outputPath)
                ? File.ReadAllBytes(outputPath)
                : null;
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

}
