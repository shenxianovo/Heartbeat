using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

public sealed class LocalApplicationIdentityTests
{
    [Test, Property("caseId", "foreground-local-application-identity")]
    public async Task TwoObserversShareInstallationButAnotherDeviceAndDeletionCreateNewEntities()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(db.ConnectionString);
        using var http = factory.CreateClient();
        var first = await ForegroundTestStore.CreateAsync(http);
        var second = await ForegroundTestStore.CreateAsync(http, first.Device);
        foreach (var collector in new[] { first, second }) { collector.Store.Checkpoint(collector.Capture(), null); collector.Store.FinishActive(); }
        var apps = await first.Data.ListAsync("entities?resourceName=local-applications");
        await Assert.That(apps.Count).IsEqualTo(1);
        await Assert.That((await first.Observations()).Length).IsEqualTo(2);
        await Assert.That((await first.Data.ListAsync("entities?resourceName=devices")).Count).IsEqualTo(1);
        await first.Data.WriteAsync((first.Device with { Name = "renamed device" }).Write);
        first.Store.Checkpoint(first.Capture(1), null); first.Store.FinishActive();
        var afterRename = await first.Data.ListAsync("entities?resourceName=local-applications");
        await Assert.That(afterRename[0].GetProperty("versionId").GetGuid()).IsEqualTo(apps[0].GetProperty("versionId").GetGuid());
        var otherDevice = await ForegroundTestStore.CreateAsync(http);
        otherDevice.Store.Checkpoint(otherDevice.Capture(), null); otherDevice.Store.FinishActive();
        await Assert.That((await first.Data.ListAsync("entities?resourceName=local-applications")).Count).IsEqualTo(2);
        await Assert.That((await first.Data.ListAsync("entities?resourceName=devices")).Count).IsEqualTo(2);
        using var removed = await http.DeleteAsync("entities/" + apps[0].GetProperty("entityId").GetGuid()); removed.EnsureSuccessStatusCode();
        first.Store.Checkpoint(first.Capture(2), null); first.Store.FinishActive();
        var afterDeletion = await first.Data.ListAsync("entities?resourceName=local-applications");
        await Assert.That(afterDeletion.Any(v => v.GetProperty("entityId").GetGuid() == apps[0].GetProperty("entityId").GetGuid())).IsFalse();
        await Assert.That(afterDeletion.Count).IsEqualTo(2);
    }

    [Test, Property("caseId", "foreground-local-application-identity")]
    public async Task InstallationKeysUseRawPathsAndMissingPathsUseOnlyTheProcessLifetime()
    {
        var device = Guid.CreateVersion7().ToString(); var keys = new ApplicationKeys(device);
        var reading = ForegroundTimelineTests.Reading(0);
        var first = keys.Resolve(reading);
        await Assert.That(new ApplicationKeys(device).Resolve(reading with { Name = "rename" }).Key).IsEqualTo(first.Key);
        await Assert.That(keys.Resolve(reading with { ExecutablePath = "/Other" }).Key == first.Key).IsFalse();
        await Assert.That(keys.Resolve(reading with { BundleIdentifier = "other" }).Key == first.Key).IsFalse();
        var missing = reading with { ExecutablePath = null, ProcessId = 42, ProcessStartedAt = DateTimeOffset.UtcNow };
        await Assert.That(keys.Resolve(missing).Key).IsEqualTo(keys.Resolve(missing).Key);
        await Assert.That(keys.Resolve(missing).Key == new ApplicationKeys(device).Resolve(missing).Key).IsFalse();
        await Assert.That(keys.Resolve(missing with { ProcessStartedAt = null }).Key == keys.Resolve(missing with { ProcessStartedAt = null }).Key).IsFalse();
    }
}
