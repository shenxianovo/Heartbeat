using System.Security.Cryptography;
using System.Text;
using Heartbeat.Hub.Runtime;

namespace Heartbeat.Desktop;

public sealed class DesktopProfile : IDisposable
{
    public const string CredentialService = "com.shenxianovo.heartbeat.desktop";
    public static string CredentialAccount(string directory) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory))));

    private readonly ICredentialStore _credentials;
    private readonly string _account;

    public DesktopProfile(string directory, ICredentialStore credentials)
    {
        Storage = new HubLocalStorage(directory);
        _credentials = credentials;
        _account = CredentialAccount(DirectoryPath);
    }

    public HubLocalStorage Storage { get; }
    public string DirectoryPath => Storage.DirectoryPath;
    public string DatabasePath => Storage.DatabasePath;

    public DesktopSettings? ReadSettings()
    {
        var settings = Storage.ReadDocument<DesktopSettings>("settings");
        if (settings is null) return null;
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
        Storage.WriteDocument("settings", settings);
    }

    public void Dispose() => Storage.Dispose();
}
