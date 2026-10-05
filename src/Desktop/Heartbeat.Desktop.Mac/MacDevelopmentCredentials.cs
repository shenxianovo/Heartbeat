using System.Runtime.Versioning;
using System.Text;

namespace Heartbeat.Desktop.Mac;

/// <summary>One development profile owns its private credential file; never a Keychain fallback.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacDevelopmentCredentials : ICredentialStore
{
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryModeBits = FileModeBits | UnixFileMode.UserExecute;
    private readonly string _directory;
    private readonly string _path;
    private readonly string _account;

    public MacDevelopmentCredentials(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "api-key");
        _account = DesktopProfile.CredentialAccount(_directory);
        RequireRegularPath(_directory);
        Directory.CreateDirectory(_directory, DirectoryModeBits);
        File.SetUnixFileMode(_directory, DirectoryModeBits);
    }

    public string? Read(string account)
    {
        RequireProfile(account);
        if (!File.Exists(_path)) return null;
        if (File.GetUnixFileMode(_path) != FileModeBits)
            throw new IOException("开发凭据文件权限必须为 0600。");
        return File.ReadAllText(_path);
    }

    public void Write(string account, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        RequireProfile(account);
        var temporary = Path.Combine(_directory, $".api-key-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = FileModeBits }))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(secret);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private void RequireProfile(string account)
    {
        if (account != _account) throw new ArgumentException("凭据不属于此开发配置目录。", nameof(account));
        RequireRegularPath(_directory);
        RequireRegularPath(_path);
        if (Directory.Exists(_path)) throw new IOException("开发凭据路径必须是普通文件。");
    }

    private static void RequireRegularPath(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("开发凭据不能使用符号链接。");
    }
}
