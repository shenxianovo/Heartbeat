using Heartbeat.Collector.VRChat;
using Heartbeat.Hub;
using Heartbeat.Hub.Host;
using Heartbeat.Hub.Runtime;

// The deployable application chooses installed collectors; Hub stays independent of them.
await using var app = HubApplication.Create(args, services =>
    services.AddSingleton<ICollectorFactory>(provider => new VRChatCollectorFactory(
        provider.GetRequiredService<IHubSubmissionClient>(), provider.GetRequiredService<LocalSecretStore>(),
        provider.GetRequiredService<IConfiguration>()["VRChat:Contact"] ?? "https://github.com/shenxianovo/heartbeat")));
await app.RunAsync();
