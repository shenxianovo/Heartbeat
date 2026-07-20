namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>
    /// 建/改 Strand 的提交（ADR-028 §5，成员单位随 ADR-029 换 Matcher）：带 Id 按 Id 定位（可改名），
    /// 无 Id 按 (Owner, Name) 收敛——重复提交幂等，不产重复行。成员整组替换。
    /// </summary>
    public class BindStrandRequest
    {
        public Guid? Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Gloss { get; set; } = string.Empty;

        public List<MatcherDto> Members { get; set; } = [];
    }
}
