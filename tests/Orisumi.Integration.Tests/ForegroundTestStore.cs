using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;
using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

internal sealed class ForegroundTestStore(DataClient data, DeviceContext device)
{
    internal DataClient Data { get; } = data;
    internal DeviceContext Device { get; } = device;
    internal List<string> Diagnostics { get; } = [];
    internal ForegroundStore Store { get; private set; } = null!;
    internal ApplicationKeys Applications { get; } = new(device.Key.Value);
    internal static async Task<ForegroundTestStore> CreateAsync(HttpClient http, DeviceContext? device = null)
    {
        device ??= new(new(null, "test.device", Guid.NewGuid().ToString()), "device", "macos");
        var writer = new WriteClient(http); await writer.InitializeAsync();
        var data = new DataClient(http, http, Ref.Entity(writer.ObserverId.Value)) { Device = device };
        await ForegroundRegistration.RegisterAsync(data);
        var fixture = new ForegroundTestStore(data, device);
        fixture.Store = new(data, device, fixture.Diagnostics.Add);
        return fixture;
    }
    internal async Task<EntityVersion[]> Observations() => (await Data.ListAsync("observations?resourceName=foreground-application-periods")).Select(VersionJson.ReadSaved).ToArray();
    internal async Task<EntityVersion[]> Periods() => (await Data.ListAsync("entities?resourceName=foreground-application-periods")).Select(VersionJson.ReadSaved).ToArray();
    internal ForegroundCapture Capture(int second = 0) => ForegroundCapture.Create(Applications.Resolve(ForegroundTimelineTests.Reading(second)), ForegroundTimelineTests.Reading(second));
}
