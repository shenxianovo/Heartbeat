namespace Heartbeat.Server.Entities;

/// <summary>已提交 App merge 的幂等回执。源 App 删除后仍可识别同一重试。</summary>
public class AppMergeReceipt
{
    public long Id { get; set; }
    public string SourceAppKey { get; set; } = string.Empty;
    public string TargetAppKey { get; set; } = string.Empty;
    public long TargetAppId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string ResponseJson { get; set; } = string.Empty;
}
