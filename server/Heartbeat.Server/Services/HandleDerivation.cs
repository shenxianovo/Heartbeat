using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// segment → 粗把手 (Source, Token) 的派生（ADR-028 §3，纯函数）。
    /// Token 取该 Source 最自然的粗身份：browser→domain、system→AppName、vscode→仓库根。
    /// 是 IdentityKey 的粗化——判据可细（origin+path），知识挂载粒度粗。
    /// </summary>
    public static class HandleDerivation
    {
        /// <summary>派生不出把手（未知 Source、缺关键字段）返回 null：该段不参与知识层。</summary>
        public static (string Source, string Token)? Derive(
            string source, string? appName, string? attributesJson, string identityKey)
        {
            return source switch
            {
                ActivitySources.System =>
                    string.IsNullOrWhiteSpace(appName) ? null : (source, appName),
                ActivitySources.Browser =>
                    DeriveBrowserDomain(attributesJson, identityKey) is { } domain ? (source, domain) : null,
                // vscode 采集器未落地：仓库根的 Attributes 形状待定，落地时补分支。
                _ => null,
            };
        }

        private static string? DeriveBrowserDomain(string? attributesJson, string identityKey)
        {
            // 首选扩展侧算好的 Attributes.domain；缺失/损坏时从完整 url 取 host。
            if (!string.IsNullOrEmpty(attributesJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(attributesJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetNonEmptyString(doc.RootElement, "domain", out var domain))
                            return domain;
                        if (TryGetNonEmptyString(doc.RootElement, "url", out var url)
                            && Uri.TryCreate(url, UriKind.Absolute, out var fromUrl)
                            && fromUrl.Host.Length > 0)
                            return fromUrl.Host;
                    }
                }
                catch (JsonException)
                {
                    // Attributes 损坏不致命：退化到 IdentityKey。
                }
            }

            // 兜底：IdentityKey 是规范化 URL（origin+pathname），可取 host。
            return Uri.TryCreate(identityKey, UriKind.Absolute, out var uri) && uri.Host.Length > 0
                ? uri.Host
                : null;
        }

        private static bool TryGetNonEmptyString(JsonElement obj, string property, out string value)
        {
            value = string.Empty;
            if (obj.TryGetProperty(property, out var el)
                && el.ValueKind == JsonValueKind.String
                && el.GetString() is { Length: > 0 } s)
            {
                value = s;
                return true;
            }
            return false;
        }
    }
}
