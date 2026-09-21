using System.Text.Json;
using Heartbeat.Collector.VRChat;
using Heartbeat.Hub.Runtime;

namespace Heartbeat.Hub.Tests;

public sealed class VRChatCollectorTests
{
    private const string Account = "usr_11111111-1111-4111-8111-111111111111";

    [Fact]
    public void PresenceExtendsSameRecordWithoutChangingValueAndNeverBridgesMissingObservations()
    {
        var projector = new PresenceRecords(Account, TimeSpan.FromMinutes(2));
        var at = DateTimeOffset.UtcNow;
        var presence = new VRChatPresence("world", "Name", "instance", Account);
        var first = projector.Observe(presence, at)!;
        var continued = projector.Observe(presence with { WorldName = "Changed" }, at.AddMinutes(1))!;
        Assert.Equal(first.Id, continued.Id);
        Assert.Equal(first.Value.GetRawText(), continued.Value.GetRawText());
        Assert.Equal(at.AddMinutes(1), continued.EndedAt);
        Assert.Null(projector.Observe(null, at.AddMinutes(2)));
        var resumed = projector.Observe(presence, at.AddMinutes(3))!;
        Assert.NotEqual(first.Id, resumed.Id);
        Assert.NotEqual(resumed.Id, projector.Observe(presence, at.AddMinutes(8))!.Id);
        Assert.Throws<InvalidDataException>(() => projector.Observe(presence with { ObservedAccountId = "other" }, at.AddMinutes(9)));
    }

    [Fact]
    public async Task TwoFactorFlowStoresOnlySessionAndSubmitsCurrentRecordsToRealSqliteCustody()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var queue = new RecordOutbox(Path.Combine(directory.FullName, "hub.sqlite"),
                new DeliveryDestination(new Uri("https://backend.example"), Guid.NewGuid()));
            var factory = new FakeApi();
            var secrets = new LocalSecretStore(Path.Combine(directory.FullName, "secrets"));
            await using var collector = new VRChatCollector(Account, factory, new LocalHubSubmissionClient(queue), secrets);
            var config = await collector.ConfigureAsync(JsonSerializer.SerializeToElement(new { username = "user", password = "private-password" }), TestContext.Current.CancellationToken);
            Assert.Equal("authentication_required", collector.State.State);
            Assert.DoesNotContain("private-password", config.GetRawText());
            await collector.ConfigureAsync(JsonSerializer.SerializeToElement(new { code = "123456" }), TestContext.Current.CancellationToken);
            Assert.Equal("paused", collector.State.State);
            await collector.StartAsync(TestContext.Current.CancellationToken);
            await collector.PauseAsync(TestContext.Current.CancellationToken);
            var record = Assert.Single(queue.TakePending());
            Assert.Equal(Account, record.Route.Collector.Target);
            Assert.Equal("vrchat.location", record.Route.Track.Type);
            Assert.Equal("world", record.Record.Value.GetProperty("world_id").GetString());
            Assert.Equal("session", secrets.Read($"vrchat:{Account}:session"));
            Assert.True(factory.Restored);
            await collector.RemoveAsync(TestContext.Current.CancellationToken);
            Assert.Null(secrets.Read($"vrchat:{Account}:session"));
            Assert.Single(queue.TakePending());
        }
        finally { directory.Delete(true); }
    }

    private sealed class FakeApi : IVRChatApiFactory, IVRChatApiSession
    {
        private bool _verified;
        public bool Restored { get; private set; }
        public IVRChatApiSession FromCredentials(string username, string password) => this;
        public IVRChatApiSession FromSession(string serializedSession) { Restored = true; return this; }
        public Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VRChatAuthenticationState("Example", _verified ? [] : ["emailOtp"], _verified ? Account : null));
        public Task VerifyTwoFactorAsync(string method, string code, CancellationToken cancellationToken)
        { Assert.Equal("emailOtp", method); Assert.Equal("123456", code); _verified = true; return Task.CompletedTask; }
        public Task<VRChatPresence?> GetPresenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult<VRChatPresence?>(new("world", null, "instance", Account));
        public Task<string?> GetWorldNameAsync(string worldId, CancellationToken cancellationToken) => Task.FromResult<string?>("World");
        public string ExportSession() => "session";
    }
}
