namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>一条候选提问（ADR-029 §4）：锚定一个 Matcher 提案 + AI 的一次性名字/释义提案，供表单确认。</summary>
    public class QuestionItemResponse
    {
        /// <summary>观测指纹提案（规范化后的路径谓词）。绑定/Mute 的锚。</summary>
        public MatcherDto Matcher { get; set; } = new();

        /// <summary>向用户提的问题文本。</summary>
        public string Question { get; set; } = string.Empty;

        /// <summary>判官在 digest 里看到的依据（时段 + 组合）。</summary>
        public string Evidence { get; set; } = string.Empty;

        /// <summary>AI 猜的名字。空 = 没把握，用户纯手填。</summary>
        public string ProposedName { get; set; } = string.Empty;

        /// <summary>AI 猜的一句话释义。空同上。</summary>
        public string ProposedGloss { get; set; } = string.Empty;
    }

    /// <summary>当日候选提问集（ADR-029 §4）：每天封顶 3 条。空 = 今天没有值得问的。</summary>
    public class DailyQuestionsResponse
    {
        public List<QuestionItemResponse> Questions { get; set; } = [];
    }
}
