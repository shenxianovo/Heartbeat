using Heartbeat.Observers.ForegroundState;

namespace Heartbeat.Integration.Tests;

public sealed class MacObserverIdentityTests
{
    private string _directory = null!;

    [Before(Test)]
    public void CreateDirectory() => _directory = Directory.CreateTempSubdirectory("heartbeat-identity-").FullName;

    [After(Test)]
    public void RemoveDirectory() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task RestartKeepsIdentityAndAnotherFileSelectsAnotherInstance()
    {
        var path = Path.Combine(_directory, "instance", "identity.json");
        var first = ForegroundStateIdentity.LoadOrCreate(path);
        await Assert.That(ForegroundStateIdentity.LoadOrCreate(path)).IsEqualTo(first);
        await Assert.That(ForegroundStateIdentity.LoadOrCreate(Path.Combine(_directory, "another.json")) == first)
            .IsFalse();
        await Assert.That(first.Value.Version).IsEqualTo(7);
    }

    [Test]
    public async Task ConcurrentFirstStartsPublishOneCompleteIdentity()
    {
        var path = Path.Combine(_directory, "identity.json");
        var identities = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ForegroundStateIdentity.LoadOrCreate(path))));
        await Assert.That(identities.Distinct().Count()).IsEqualTo(1);
        await Assert.That(ForegroundStateIdentity.LoadOrCreate(path)).IsEqualTo(identities[0]);
        await Assert.That(Directory.GetFiles(_directory, "*.tmp").Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("{")]
    [Arguments("{}")]
    [Arguments("{\"observerId\":null}")]
    [Arguments("{\"observerId\":\"8dcc4596-2650-4a1e-a998-53a5e7dc626a\"}")]
    [Arguments("{\"observerId\":\"01a114c5-6282-7385-0b65-b93517e16b0f\"}")]
    public async Task CorruptIdentityIsReportedAndNotReplaced(string content)
    {
        var path = Path.Combine(_directory, "identity.json");
        await File.WriteAllTextAsync(path, content);
        await Assert.That(() => ForegroundStateIdentity.LoadOrCreate(path)).Throws<InvalidDataException>();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(content);
    }
}
