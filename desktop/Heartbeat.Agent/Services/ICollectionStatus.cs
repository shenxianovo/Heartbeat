namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 集面读模型（ADR-021）：hub 维护的本机采集面状态读表面。
    /// WPF 显示与 presence 心跳只消费这里，不伸手进采集器。
    /// 与出网 buffer 分离：不随 drain 清空。
    /// </summary>
    public interface ICollectionStatus
    {
        /// <summary>Current Activity：此刻在干什么。away 原样暴露（__away__），无前台为 null。</summary>
        string? CurrentApp { get; }

        /// <summary>Current Activity 变更通知。值实际变化才触发；转场点由 system 采集器推送，进程内零延迟。</summary>
        event Action<string?>? CurrentAppChanged;

        /// <summary>per-Source last-seen（Accept 时刻戳）：Active 的机制（CONTEXT.md）。返回快照副本。</summary>
        IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen { get; }
    }
}
