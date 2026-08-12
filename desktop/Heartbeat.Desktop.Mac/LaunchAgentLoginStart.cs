using System.Security;

namespace Heartbeat.Desktop.Mac;

public interface IMacLoginStart
{
    bool IsEnabled { get; }
    void Enable(string executablePath);
    void Disable();
}

/// <summary>每用户 LaunchAgent adapter；只写 ~/Library，不需要管理员权限。</summary>
public sealed class LaunchAgentLoginStart : IMacLoginStart
{
    private readonly string _plistPath;

    public LaunchAgentLoginStart(string? plistPath = null)
    {
        _plistPath = plistPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", "com.shenxianovo.heartbeat.plist");
    }

    public bool IsEnabled => File.Exists(_plistPath);

    public void Enable(string executablePath)
    {
        var escaped = SecurityElement.Escape(executablePath)
            ?? throw new ArgumentException("Executable path is invalid.", nameof(executablePath));
        var plist = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>com.shenxianovo.heartbeat</string>
              <key>ProgramArguments</key>
              <array><string>{{escaped}}</string></array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><false/>
            </dict>
            </plist>
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(_plistPath)!);
        var temporary = _plistPath + ".tmp";
        File.WriteAllText(temporary, plist);
        File.Move(temporary, _plistPath, overwrite: true);
    }

    public void Disable()
    {
        if (File.Exists(_plistPath))
            File.Delete(_plistPath);
    }
}
