using Heartbeat.Hub;
using Heartbeat.Hub.Runtime;
using Heartbeat.Management;

namespace Heartbeat.Collector.VRChat;

public sealed class VRChatCollectorFactory(IHubSubmissionClient hub, LocalSecretStore secrets, string contact) : ICollectorFactory
{
    public const string Key = "heartbeat.collector.vrchat";
    internal static readonly CollectorField[] LoginFields = [
        new("username", "VRChat 用户名或邮箱", "text", true),
        new("password", "密码", "secret", true),
    ];
    public CollectorType Type { get; } = new(Key, "VRChat", LoginFields);

    public ICollectorSession Create(string? target) =>
        new VRChatCollector(target, new VRChatApiFactory("Heartbeat", "0.1.0", contact), hub, secrets);
}
