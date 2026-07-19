namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// Strand 的成员把手（ADR-028 §3）：(Source, Token) 按值存，Token 为该 Source 的粗身份
    /// （browser→domain、system→AppName、vscode→仓库根）。
    /// Anchor/Satellite 强度不落库——投影时从摊布推断。
    /// </summary>
    public class StrandHandle
    {
        public long Id { get; set; }

        public Guid StrandId { get; set; }

        public string Source { get; set; } = string.Empty;

        public string Token { get; set; } = string.Empty;

        public Strand Strand { get; set; } = null!;
    }
}
