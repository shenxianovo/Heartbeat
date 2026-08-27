using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public interface ICollectorSecretStore
{
    ValueTask<string?> ReadAsync(
        Guid collectorInstanceId,
        string key,
        CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        Guid collectorInstanceId,
        string key,
        string value,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        Guid collectorInstanceId,
        string key,
        CancellationToken cancellationToken = default);
}

public sealed class EncryptedFileCollectorSecretStore : ICollectorSecretStore
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _directory;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedFileCollectorSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _keyPath = Path.Combine(_directory, "collector-secret.key");
    }

    public async ValueTask<string?> ReadAsync(
        Guid collectorInstanceId,
        string key,
        CancellationToken cancellationToken = default)
    {
        Validate(collectorInstanceId, key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = SecretPath(collectorInstanceId, key);
            if (!File.Exists(path))
                return null;
            var envelope = JsonSerializer.Deserialize<SecretEnvelope>(
                await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new CryptographicException("Collector Secret envelope is empty.");
            var encryptionKey = await LoadOrCreateKeyAsync(cancellationToken);
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            var tag = Convert.FromBase64String(envelope.Tag);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(encryptionKey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(collectorInstanceId, key));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(
        Guid collectorInstanceId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        Validate(collectorInstanceId, key);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var encryptionKey = await LoadOrCreateKeyAsync(cancellationToken);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plaintext = Encoding.UTF8.GetBytes(value);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(encryptionKey, TagSize))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(collectorInstanceId, key));
            var envelope = new SecretEnvelope(
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag));
            var path = SecretPath(collectorInstanceId, key);
            var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(envelope),
                new UTF8Encoding(false),
                cancellationToken);
            RestrictPermissions(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(
        Guid collectorInstanceId,
        string key,
        CancellationToken cancellationToken = default)
    {
        Validate(collectorInstanceId, key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = SecretPath(collectorInstanceId, key);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> LoadOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        if (File.Exists(_keyPath))
        {
            var existing = Convert.FromBase64String(await File.ReadAllTextAsync(_keyPath, cancellationToken));
            return existing.Length == KeySize
                ? existing
                : throw new CryptographicException("Collector Secret key has an invalid length.");
        }

        var created = RandomNumberGenerator.GetBytes(KeySize);
        await File.WriteAllTextAsync(
            _keyPath,
            Convert.ToBase64String(created),
            new UTF8Encoding(false),
            cancellationToken);
        RestrictPermissions(_keyPath);
        return created;
    }

    private string SecretPath(Guid collectorInstanceId, string key)
    {
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory, $"{collectorInstanceId:N}-{keyHash}.secret.json");
    }

    private static byte[] AssociatedData(Guid collectorInstanceId, string key) =>
        Encoding.UTF8.GetBytes($"heartbeat.collector-secret/1/{collectorInstanceId:D}/{key}");

    private static void Validate(Guid collectorInstanceId, string key)
    {
        if (collectorInstanceId == Guid.Empty)
            throw new ArgumentException("Collector Instance ID must not be empty.", nameof(collectorInstanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }

    private static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record SecretEnvelope(string Nonce, string Ciphertext, string Tag);
}
