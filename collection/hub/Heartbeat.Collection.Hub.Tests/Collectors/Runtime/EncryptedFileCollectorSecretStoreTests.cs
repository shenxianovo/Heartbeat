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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
