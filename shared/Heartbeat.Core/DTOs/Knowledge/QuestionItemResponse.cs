namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>一条候选提问（ADR-028 §4/§5）：一簇共现把手 + AI 的一次性提案，供表单确认。</summary>
    public class QuestionItemResponse
    {
        /// <summary>推断的锚点把手：提案命名的主体。</summary>
        public HandleDto Anchor { get; set; } = new();

        /// <summary>簇内全部成员（含 Anchor），表单预勾、可去勾。</summary>
        public List<HandleDto> Handles { get; set; } = [];

        public double TotalSeconds { get; set; }

        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }

        /// <summary>AI 猜的名字。空 = 猜不出/LLM 降级，用户纯手填。</summary>
        public string ProposedName { get; set; } = string.Empty;

        /// <summary>AI 猜的一句话释义。空同上。</summary>
        public string ProposedGloss { get; set; } = string.Empty;
    }
}
