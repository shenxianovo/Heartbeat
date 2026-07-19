namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>当日候选提问集（ADR-028 §4）：每天封顶 1–3 条。空 = 今天没有值得问的疑惑簇。</summary>
    public class DailyQuestionsResponse
    {
        public List<QuestionItemResponse> Questions { get; set; } = [];
    }
}
