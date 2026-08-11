using System.Text.RegularExpressions;

namespace Heartbeat.Core;

/// <summary>AppIdentity 与产品短键的跨上下文规范化规则（ADR-034）。</summary>
public static partial class AppIdentityKeys
{
    public const string WindowsPrefix = "win:";
    public const string MacPrefix = "mac:";
    public const string SyntheticPrefix = "sys:";

    public static string FromLegacyWindowsAppName(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var value = appName.Trim();
        if (string.Equals(value, SyntheticApps.Away, StringComparison.OrdinalIgnoreCase))
            return SyntheticPrefix + "away";

        return WindowsPrefix + StripExe(value).ToLowerInvariant();
    }

    public static string Normalize(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var trimmed = key.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator == trimmed.Length - 1)
            throw new ArgumentException("AppIdentity key must use win:, mac:, or sys: prefix.", nameof(key));

        var prefix = trimmed[..separator].ToLowerInvariant();
        var value = trimmed[(separator + 1)..].Trim().ToLowerInvariant();
        if (prefix == "win") value = StripExe(value);
        if (prefix is not ("win" or "mac" or "sys") || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("AppIdentity key must use win:, mac:, or sys: prefix.", nameof(key));

        return prefix + ":" + value;
    }

    /// <summary>从平台身份推导 provisional App 的短键；这不是产品自动归并。</summary>
    public static string ProvisionalProductKey(string identityKey)
    {
        var normalized = Normalize(identityKey);
        var separator = normalized.IndexOf(':');
        var platform = normalized[..separator];
        var value = normalized[(separator + 1)..];
        var candidate = platform == "mac" ? value.Split('.').Last() : value;
        return ProductSlug(candidate);
    }

    /// <summary>仅在短键真实碰撞时使用的平台限定候选。</summary>
    public static string QualifiedProductKey(string identityKey)
    {
        var normalized = Normalize(identityKey);
        var separator = normalized.IndexOf(':');
        var platform = normalized[..separator];
        var value = normalized[(separator + 1)..];

        if (platform == "mac")
        {
            var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return ProductSlug(parts[^2] + "." + parts[^1]);
        }

        return ProductSlug(platform + "." + value);
    }

    public static string ProductSlug(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var slug = NonSlugCharacters().Replace(StripExe(value.Trim().ToLowerInvariant()), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "app" : slug;
    }

    private static string StripExe(string value)
        => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    [GeneratedRegex("[^a-z0-9.]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugCharacters();
}
