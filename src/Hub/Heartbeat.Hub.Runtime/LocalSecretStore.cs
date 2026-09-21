using System.Security.Cryptography;
using System.Text;

namespace Heartbeat.Hub.Runtime;

// Like the previous collector store, encryption is backed by a protected local key.
// It prevents secrets entering config/record files, not access by the same OS account.
public sealed class LocalSecretStore
{
    private readonly string _directory;
    private readonly byte[] _key;

    public LocalSecretStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "key");
        if (!File.Exists(path)) WriteProtected(path, RandomNumberGenerator.GetBytes(32));
        _key = File.ReadAllBytes(path);
        if (_key.Length != 32) throw new InvalidDataException("Invalid local secret key.");
    }

    public string? Read(string name)
    {
        var path = SecretPath(name);
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        if (data.Length < 28) throw new InvalidDataException("Invalid secret file.");
        var clear = new byte[data.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), clear, Encoding.UTF8.GetBytes(name));
        return Encoding.UTF8.GetString(clear);
    }

    public void Write(string name, string value)
    {
        var clear = Encoding.UTF8.GetBytes(value);
        var data = new byte[28 + clear.Length];
        RandomNumberGenerator.Fill(data.AsSpan(0, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(data.AsSpan(0, 12), clear, data.AsSpan(28), data.AsSpan(12, 16), Encoding.UTF8.GetBytes(name));
        var path = SecretPath(name);
        var temporary = path + ".tmp";
        WriteProtected(temporary, data);
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete(string name) => File.Delete(SecretPath(name));
    private string SecretPath(string name) => Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name))));
    private static void WriteProtected(string path, byte[] value)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(value);
        stream.Flush(flushToDisk: true);
    }
}
