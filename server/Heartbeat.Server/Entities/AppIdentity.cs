namespace Heartbeat.Server.Entities
{
    /// <summary>
    /// 平台可直接观测到的应用身份。Key 是全局产品事实，不按 Owner 分区；
    /// 多个身份可显式映射到同一个跨平台 App。
    /// </summary>
    public class AppIdentity
    {
        public long Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public long AppId { get; set; }
        public App App { get; set; } = null!;
    }
}
