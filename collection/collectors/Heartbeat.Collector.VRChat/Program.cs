using Heartbeat.Collector.VRChat;
using Heartbeat.Collection.CollectorProtocol;

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
var definition = new CollectorClientDefinition(
    "vrchat.managed",
    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
    {
        ["facts.segment"] = [1],
        ["auth.interactive"] = [1],
        ["secrets.instance"] = [1],
        ["resources.instance-data"] = [1],
        ["diagnostics.stream-gap"] = [1]
    },
    "account",
    [new CollectorOutputBinding(
        "presence",
        "presence",
        new Dictionary<string, string>(StringComparer.Ordinal))],
    OutboxCapacity: 512);
await using var binding = StdioCollectorProtocolBinding.FromEnvironment(Console.In, Console.Out);
await using var client = new CollectorProtocolClient(definition, binding);
await client.RunAsync(new VRChatManagedCollector(apiFactory));
