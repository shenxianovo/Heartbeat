using System.Text;
using System.Text.Json;

namespace Heartbeat.Server.Services;

/// <summary>
/// 读未验签的 JWT header 判断令牌种类，用于在两个 JwtBearer scheme 间路由。
/// 只做路由不做校验——被选中的 scheme 仍会完整验签名/issuer/有效期。
/// OIDC access token（Web 用户）typ 为 "at+jwt"（RFC 9068）；
/// Agent 经 apikeys/exchange 拿到的会话 JWT typ 为 "JWT"。
/// </summary>
public static class JwtTypeSniffer
{
    public static bool IsOidcAccessToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0) return false;

        try
        {
            var header = token[..dot].Replace('-', '+').Replace('_', '/');
            header = header.PadRight(header.Length + (4 - header.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header)));
            return doc.RootElement.TryGetProperty("typ", out var typ)
                && string.Equals(typ.GetString(), "at+jwt", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
