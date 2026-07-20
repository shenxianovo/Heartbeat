namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>绑定提交后的 Strand 回读。</summary>
    public class StrandResponse
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Gloss { get; set; } = string.Empty;

        public List<MatcherDto> Members { get; set; } = [];

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
