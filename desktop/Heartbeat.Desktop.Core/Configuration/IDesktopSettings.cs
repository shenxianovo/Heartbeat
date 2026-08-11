namespace Heartbeat.Desktop.Core.Configuration;

/// <summary>Desktop.Core 所需的最小配置表面；持久化格式由 platform head 所有。</summary>
public interface IDesktopSettings
{
    IReadOnlyList<string> AwayProcessNames { get; }
    /// <summary>
    /// true 使用跨平台最终语义（focused-window 必切段）；false 保持旧 Windows 输出，
    /// 同 App 的 focused-window 仍按旧的标题/Interaction Signal 规则处理。
    /// </summary>
    bool SplitFocusedWindowChangesUnconditionally { get; }
    event Action<IReadOnlyList<string>>? AwayProcessNamesChanged;
}
