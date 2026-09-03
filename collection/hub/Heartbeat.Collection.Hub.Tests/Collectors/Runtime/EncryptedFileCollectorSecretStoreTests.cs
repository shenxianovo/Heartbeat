using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Runtime;

public sealed class EncryptedFileCollectorSecretStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-collector-secrets-{Guid.NewGuid():N}");

    [Fact]
    public async Task SecretRoundTrip_IsEncryptedAndIsolatedByCollectorInstance()
    {
        var store = new EncryptedFileCollectorSecretStore(_directory);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await store.WriteAsync(first, "session", "auth-cookie-plain-text");

        Assert.Equal("auth-cookie-plain-text", await store.ReadAsync(first, "session"));
        Assert.Null(await store.ReadAsync(second, "session"));
        Assert.DoesNotContain(
            "auth-cookie-plain-text",
            string.Join('\n', Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText)),
            StringComparison.Ordinal);

        await store.DeleteAsync(first, "session");

        Assert.Null(await store.ReadAsync(first, "session"));
    }

    [Fact]
    public async Task LegacySecretEnvelopeWithoutSchemaVersion_RemainsReadable()
    {
        var store = new EncryptedFileCollectorSecretStore(_directory);
        var collectorInstanceId = Guid.CreateVersion7();
        await store.WriteAsync(collectorInstanceId, "session", "legacy-secret");
        var envelopePath = Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories).Single();
        using var current = JsonDocument.Parse(await File.ReadAllTextAsync(envelopePath));
        var root = current.RootElement;
        await File.WriteAllTextAsync(
            envelopePath,
            JsonSerializer.Serialize(new
            {
                Nonce = root.GetProperty("nonce").GetString(),
                Ciphertext = root.GetProperty("ciphertext").GetString(),
                Tag = root.GetProperty("tag").GetString()
            }));

        Assert.Equal("legacy-secret", await store.ReadAsync(collectorInstanceId, "session"));
    }

    [Fact]
    public async Task DeleteInstance_RemovesEverySecretInOnlyThatNamespace()
    {
        var store = new EncryptedFileCollectorSecretStore(_directory);
        var removed = Guid.CreateVersion7();
        var retained = Guid.CreateVersion7();
        await store.WriteAsync(removed, "one", "first");
        await store.WriteAsync(removed, "two", "second");
        await store.WriteAsync(retained, "one", "retained");

        await store.DeleteInstanceAsync(removed);

        Assert.Null(await store.ReadAsync(removed, "one"));
        Assert.Null(await store.ReadAsync(removed, "two"));
        Assert.Equal("retained", await store.ReadAsync(retained, "one"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
