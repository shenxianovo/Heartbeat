namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 出网源：Upload Stream 的上游 buffer 形状。segments 的 hub 缓冲与
    /// input-events 缓冲是两个生产 adapter——出网侧从此只认识这一套词汇。
    /// Drain 是破坏性取走；Reinject 接收既没送达也没缓存住的退回批
    /// （ADR-020 上传通道契约），保证 drain 后的批不静默蒸发。
    /// </summary>
    public interface IUploadSource<T>
    {
        /// <summary>取走当前缓冲的全部项并清空。</summary>
        List<T> Drain();

        /// <summary>退回重注入：双失败（送不达且缓存不住）的批回到源，下轮重试。</summary>
        void Reinject(List<T> items);
    }
}
