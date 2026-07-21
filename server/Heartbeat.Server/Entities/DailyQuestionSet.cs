namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 当日发问缓存（ADR-029 §4）：与 recap 同构——按 (Owner, 日窗口) 一份，跟段水位，失败不写。
    /// PayloadJson 为封顶后的问题卡列表；读取时对已裁决 Matcher 做确定性 diff 过滤（零 LLM 重调）。
    /// </summary>
    public class DailyQuestionSet
    {
        public long Id { get; set; }

        public string OwnerId { get; set; } = string.Empty;

        /// <summary>日窗口起点（UTC）。缓存身份的一半。</summary>
        public DateTimeOffset WindowStart { get; set; }

        /// <summary>生成时消费到的最新 segment 时间（裁剪到窗口）。今日新鲜度水位。</summary>
        public DateTime SegmentWatermark { get; set; }

        public DateTimeOffset GeneratedAt { get; set; }

        /// <summary>序列化的 QuestionItemResponse 列表。</summary>
        public string PayloadJson { get; set; } = string.Empty;
    }
}
