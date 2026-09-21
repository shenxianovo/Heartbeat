using Heartbeat.Hub;
using Heartbeat.Hub.Runtime;
using Heartbeat.Management;

namespace Heartbeat.Collector.VRChat;

public sealed class VRChatCollectorFactory(IHubSubmissionClient hub, LocalSecretStore secrets, string contact) : ICollectorFactory
{
    public const string Key = "heartbeat.collector.vrchat";
    public CollectorType Type { get; } = new(Key, "VRChat", [
        new("displayName", "名称", "text"),
        new("username", "VRChat 用户名或邮箱", "text"),
        new("password", "密码（仅用于本次登录）", "secret"),
        new("code", "两步验证码（提示需要时填写）", "secret"),
        new("pollIntervalSeconds", "采样间隔（秒，至少 60）", "number"),
    ], "VRChat 用户 ID（usr_…）");

    public IManagedCollector Create(string target)
    {
        if (!target.StartsWith("usr_", StringComparison.Ordinal) || !Guid.TryParseExact(target[4..], "D", out var id) ||
            target != $"usr_{id:D}") throw new ArgumentException("请输入规范的 VRChat 用户 ID（usr_ 加小写 UUID）。");
        return new VRChatCollector(target, new VRChatApiFactory("Heartbeat", "0.1.0", contact), hub, secrets);
    }
}
