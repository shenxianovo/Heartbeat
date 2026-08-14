namespace Heartbeat.Collector.System.Input
{
    /// <summary>
    /// 输入活动信号：记录最近一次"可能导致窗口/标题切换"的输入（当前仅鼠标/触摸板点击）的时刻，
    /// 供标题变化门控读取（详见 ADR-016）。打字与滚轮不计入。
    /// 用单调时钟（TickCount64），只关心"距今多久"，不受系统时钟调整影响。
    /// </summary>
    public interface IInputActivitySignal
    {
        /// <summary>标记一次点击发生（由输入采集路径调用）。</summary>
        void MarkClick();

        /// <summary>距上次点击是否在给定窗口内（用于判定标题变化是否由点击驱动）。</summary>
        bool ClickedWithin(TimeSpan window);
    }

    public sealed class InputActivitySignal : IInputActivitySignal
    {
        // -1 表示从未点击过；用 long 存 TickCount64。
        private long _lastClickTicks = -1;

        public void MarkClick() => Interlocked.Exchange(ref _lastClickTicks, Environment.TickCount64);

        public bool ClickedWithin(TimeSpan window)
        {
            var last = Interlocked.Read(ref _lastClickTicks);
            if (last < 0) return false;
            return Environment.TickCount64 - last <= (long)window.TotalMilliseconds;
        }
    }
}
