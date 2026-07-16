namespace Heartbeat.Agent.Configuration
{
    /// <summary>
    /// 服务端点为编译期常量（单用户自部署，端点不进设置项）。
    /// ApiBaseUrl 可被环境变量 <see cref="ApiBaseUrlOverrideEnv"/> 覆盖，仅供本地端到端验证用。
    /// AuthServiceBaseUrl 无覆盖：本地验证只把数据打到本地 server，鉴权仍走真实 Auth 平台
    /// （token 由线上 issuer 校验，指向别处会让验证失真）。详见 README「本地端到端验证」。
    /// </summary>
    public static class Endpoints
    {
        public const string ApiBaseUrlOverrideEnv = "HEARTBEAT_API_BASE_URL";

        public static string ApiBaseUrl { get; } =
            Environment.GetEnvironmentVariable(ApiBaseUrlOverrideEnv) is { } o && !string.IsNullOrWhiteSpace(o)
                ? o.Trim().TrimEnd('/')
                : "https://heartbeat.shenxianovo.com";

        public static string AuthServiceBaseUrl { get; } = "https://auth.shenxianovo.com";
    }
}
