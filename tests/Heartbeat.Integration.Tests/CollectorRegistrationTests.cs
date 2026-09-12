using Heartbeat.Application.Recording;
using Heartbeat.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class CollectorRegistrationTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 12, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task OwnerCanRegisterCollectorInExistingTimeline()
    {
        var ownerId = Guid.Parse("019d9026-def4-74db-bf9a-f854c16a993e");
        await ProvisionTimelineAsync(ownerId);
        await using var services = CreateServices();

        var result = await RegisterAsync(
            services,
            ownerId,
            new RegisterCollectorCommand(
                "heartbeat.collector.desktop.macos",
                "  device-1  ",
                "  My Mac  "));

        var registered = Assert.IsType<RegisterCollectorResult.Registered>(result).Collector;
        Assert.Equal(7, registered.Id.Version);
        Assert.Equal("heartbeat.collector.desktop.macos", registered.Key);
        Assert.Equal("device-1", registered.Target);
        Assert.Equal("My Mac", registered.DisplayName);
        Assert.Equal(Now, registered.CreatedAt);
    }

    [Fact]
    public async Task RepeatedRegistrationReturnsStableCollectorAndUpdatesDisplayName()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var services = CreateServices();

        var first = Registered(await RegisterAsync(
            services,
            ownerId,
            new RegisterCollectorCommand("heartbeat.collector.desktop.macos", "device-1", "Old name")));
        var second = Registered(await RegisterAsync(
            services,
            ownerId,
            new RegisterCollectorCommand("heartbeat.collector.desktop.macos", "device-1", "New name")));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal("New name", second.DisplayName);
    }

    [Fact]
    public async Task SameAddressBelongsToDifferentCollectorsForDifferentOwners()
    {
        var firstOwnerId = Guid.NewGuid();
        var secondOwnerId = Guid.NewGuid();
        await ProvisionTimelineAsync(firstOwnerId);
        await ProvisionTimelineAsync(secondOwnerId);
        await using var services = CreateServices();
        var command = new RegisterCollectorCommand(
            "heartbeat.collector.desktop.macos",
            "device-1",
            "My Mac");

        var first = Registered(await RegisterAsync(services, firstOwnerId, command));
        var second = Registered(await RegisterAsync(services, secondOwnerId, command));

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task RegistrationReportsWhenOwnerHasNoTimeline()
    {
        await using var services = CreateServices();

        var result = await RegisterAsync(
            services,
            Guid.NewGuid(),
            new RegisterCollectorCommand("heartbeat.collector.desktop.macos", "device-1", "My Mac"));

        Assert.IsType<RegisterCollectorResult.TimelineNotProvisioned>(result);
    }

    [Fact]
    public async Task ConcurrentRegistrationReturnsOneStableCollector()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var services = CreateServices();

        var attempts = await Task.WhenAll(
            RegisterAsync(
                services,
                ownerId,
                new RegisterCollectorCommand("heartbeat.collector.desktop.macos", "device-1", "First name")),
            RegisterAsync(
                services,
                ownerId,
                new RegisterCollectorCommand("heartbeat.collector.desktop.macos", "device-1", "Second name")));

        var first = Registered(attempts[0]);
        var second = Registered(attempts[1]);
        Assert.Equal(first.Id, second.Id);
    }

    private ServiceProvider CreateServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Heartbeat"] = ConnectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<RegisterCollectorResult> RegisterAsync(
        IServiceProvider services,
        Guid ownerId,
        RegisterCollectorCommand command)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRegisterCollector>()
            .ExecuteAsync(ownerId, command);
    }

    private static RegisteredCollector Registered(RegisterCollectorResult result) =>
        Assert.IsType<RegisterCollectorResult.Registered>(result).Collector;
}
