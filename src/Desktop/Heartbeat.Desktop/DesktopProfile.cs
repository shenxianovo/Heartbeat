using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Desktop;

public sealed class DesktopProfile : IDisposable
{
    public const string CredentialService = "com.shenxianovo.heartbeat.desktop";
    public static string CredentialAccount(string directory) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory))));

    private readonly FileStream _ownership;
    private readonly ICredentialStore _credentials;
    private readonly string _settingsPath;
    private readonly string _account;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DesktopProfile(string directory, ICredentialStore credentials)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _ownership = new FileStream(Path.Combine(DirectoryPath, ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        _credentials = credentials;
        _settingsPath = Path.Combine(DirectoryPath, "settings.json");
        _account = CredentialAccount(DirectoryPath);
    }

    public string DirectoryPath { get; }
    public string DatabasePath => Path.Combine(DirectoryPath, "hub.sqlite");

    public DesktopSettings? ReadSettings()
    {
        if (!File.Exists(_settingsPath)) return null;
        var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(_settingsPath))
            ?? throw new InvalidDataException("客户端配置无法读取。");
        settings.Validate();
        return settings;
    }

    public string? ReadApiKey() => _credentials.Read(_account);

    public void Save(DesktopSettings settings, string apiKey)
    {
        settings.Validate();
        var previous = ReadSettings();
        if (previous is not null && (previous.Destination != settings.Destination || previous.Target != settings.Target))
            throw new InvalidOperationException("此数据目录已绑定原有 Owner、后端和 Target。更换绑定请使用独立数据目录。");
        _credentials.Write(_account, apiKey);
        var temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, _settingsPath, overwrite: true);
    }

    public void Dispose() => _ownership.Dispose();
}
