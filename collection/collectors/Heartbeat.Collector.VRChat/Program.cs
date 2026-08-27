using Heartbeat.Collector.VRChat;

if (args is ["--create-package", var packageDirectory])
{
    VRChatPackageBuilder.Create(packageDirectory);
    return;
}

IVRChatApiFactory apiFactory = Environment.GetEnvironmentVariable("HEARTBEAT_VRCHAT_MOCK") == "1"
    ? new MockVRChatApiFactory(
        int.TryParse(Environment.GetEnvironmentVariable("HEARTBEAT_VRCHAT_MOCK_TRANSIENT_POLLS"), out var failures)
            ? Math.Max(0, failures)
            : 0)
    : new VRChatApiFactory(
        "Heartbeat.Collector.VRChat",
        "0.1.0",
        Environment.GetEnvironmentVariable("HEARTBEAT_VRCHAT_CONTACT") ?? string.Empty);
var collector = new VRChatManagedCollector(Console.In, Console.Out, apiFactory);
await collector.RunAsync();
