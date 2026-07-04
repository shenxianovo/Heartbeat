namespace Heartbeat.Core
{
    /// <summary>
    /// system source 的 IdentityKey 定义。ADR-018 后 IdentityKey 不再驱动续接
    /// （续接已由稳定 Id + 服务端 upsert 取代），仅作服务端身份守卫与查询/回放分组维度。
    /// </summary>
    public static class SystemIdentity
    {
        /// <summary>
        /// 规范化 AppName + Title。AppName 不区分大小写（沿用 ADR-015 前的判据），Title 区分；
        /// null 与空标题折叠（GetWindowText 对空标题返回 null，"" 实际不会出现）。
        /// 服务端 migration 的历史数据回填 SQL 与此定义必须一致。
        /// </summary>
        public static string Key(string appName, string? title)
            => appName.ToLowerInvariant() + "\n" + (title ?? "");
    }
}
