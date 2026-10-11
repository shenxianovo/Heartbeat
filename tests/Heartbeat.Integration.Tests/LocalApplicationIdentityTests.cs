using System.Text.Json;
using Heartbeat.Observers.ForegroundState;

namespace Heartbeat.Integration.Tests;

public sealed class LocalApplicationIdentityTests
{
    private string _directory = null!;

    [Before(Test)]
    public void CreateDirectory() => _directory = Directory.CreateTempSubdirectory("heartbeat-applications-").FullName;

    [After(Test)]
    public void RemoveDirectory() => Directory.Delete(_directory, recursive: true);

    [Test]
    [Property("caseId", "foreground-local-application-identity")]
    public async Task RestartAndRenameReuseInstallationButAnotherPathOrDeviceDoesNot()
    {
        var path = Path.Combine(_directory, "applications.json");
        var reading = Read("com.example.editor", "/Applications/Editor.app/Contents/MacOS/Editor");
        var first = new LocalApplicationRegistry(path).Resolve(reading);
        var restarted = new LocalApplicationRegistry(path);
        await Assert.That(restarted.Resolve(reading with { Name = "New Name" }).Id).IsEqualTo(first.Id);
        await Assert.That(restarted.Resolve(reading with { ExecutablePath = "/Other/Editor" }).Id == first.Id).IsFalse();
        await Assert.That(restarted.Resolve(reading with { BundleIdentifier = "another.app" }).Id == first.Id).IsFalse();
        await Assert.That(new LocalApplicationRegistry(Path.Combine(_directory, "other-device.json"))
            .Resolve(reading).Id == first.Id).IsFalse();
        await Assert.That(first.Identifiers.Select(x => x.Scheme).SequenceEqual(
            ["macos.bundle-id", "macos.executable-path"])).IsTrue();
    }

    [Test]
    [Property("caseId", "foreground-local-application-identity")]
    public async Task MissingPathUsesOnlyThisProcessLifetimeAndNeverMatchesByName()
    {
        var path = Path.Combine(_directory, "applications.json");
        var registry = new LocalApplicationRegistry(path);
        var reading = Read(null, null) with { ProcessId = 42, ProcessStartedAt = DateTimeOffset.UtcNow };
        var first = registry.Resolve(reading);
        await Assert.That(registry.Resolve(reading).Id).IsEqualTo(first.Id);
        await Assert.That(registry.Resolve(reading with { ProcessStartedAt = reading.ProcessStartedAt.Value.AddSeconds(1) }).Id == first.Id).IsFalse();
        await Assert.That(new LocalApplicationRegistry(path).Resolve(reading).Id == first.Id).IsFalse();
        await Assert.That(registry.Resolve(reading with { ProcessStartedAt = null }).Id == first.Id).IsFalse();
        await Assert.That(first.Identifiers.Count).IsEqualTo(0);
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    [Property("caseId", "foreground-local-application-identity")]
    public async Task ConcurrentRegistrationsPreserveEveryApplicationAndPublishOneIdentityPerInstallation()
    {
        var path = Path.Combine(_directory, "applications.json");
        var identities = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            new LocalApplicationRegistry(path).Resolve(Read("app", "/apps/" + i % 3)).Id)));
        await Assert.That(identities.Distinct().Count()).IsEqualTo(3);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        await Assert.That(document.RootElement.GetArrayLength()).IsEqualTo(3);
        await Assert.That(Directory.GetFiles(_directory, "*.tmp").Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("{")]
    [Arguments("null")]
    [Arguments("[{}]")]
    [Arguments("[null]")]
    public async Task CorruptRegistryIsNotReplaced(string content)
    {
        var path = Path.Combine(_directory, "applications.json");
        await File.WriteAllTextAsync(path, content);
        await Assert.That(() => new LocalApplicationRegistry(path).Resolve(Read("app", "/app")))
            .Throws<InvalidDataException>();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(content);
    }

    private static ForegroundApplicationReading Read(string? bundle, string? path) =>
        new(bundle, "Editor", path, DateTimeOffset.UtcNow, "Asia/Shanghai");
}
