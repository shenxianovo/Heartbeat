using System.Text.RegularExpressions;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// The Registry URL boundary. Every URI the Runtime fetches — the index, the artifact, and every
/// redirect hop — has to resolve inside the Registry origin and inside the directory that owns it.
/// A redirect can therefore never move the download to another origin, another Package or another
/// version directory.
/// </summary>
internal static class CollectorRegistryBoundary
{
    private static readonly Regex FileNamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Normalizes the configured Registry base URI to a directory URI. A non-HTTPS base is only
    /// accepted on loopback, which is what lets local fixtures use <c>http://127.0.0.1:port/</c>
    /// while production stays HTTPS. The same-scheme rule below then keeps both self-consistent.
    /// </summary>
    public static bool TryNormalizeBase(Uri? baseUri, out Uri normalized, out string detail)
    {
        normalized = null!;
        if (baseUri is null || !baseUri.IsAbsoluteUri)
        {
            detail = "Registry base URI must be an absolute URI.";
            return false;
        }
        if (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
        {
            detail = $"Registry base URI scheme '{baseUri.Scheme}' is not http(s).";
            return false;
        }
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback)
        {
            detail = $"Registry base URI '{baseUri}' must use HTTPS unless it is a loopback fixture.";
            return false;
        }
        if (!string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            detail = $"Registry base URI '{baseUri}' must not carry user info, query or fragment.";
            return false;
        }

        normalized = baseUri.AbsolutePath.EndsWith('/')
            ? baseUri
            : new Uri(baseUri.GetLeftPart(UriPartial.Path) + "/", UriKind.Absolute);
        detail = string.Empty;
        return true;
    }

    public static Uri PackageDirectory(Uri normalizedBase, string packageId) =>
        new(normalizedBase, $"packages/{packageId}/");

    public static Uri VersionDirectory(Uri normalizedBase, string packageId, string version) =>
        new(normalizedBase, $"packages/{packageId}/versions/{version}/");

    /// <summary>
    /// True when <paramref name="candidate" /> is a single file directly inside
    /// <paramref name="directory" /> on the same origin. Cross-directory paths, traversal, encoded
    /// traversal, and foreign origins are all rejected.
    /// </summary>
    public static bool IsFileWithin(Uri directory, Uri candidate)
    {
        if (!candidate.IsAbsoluteUri ||
            !string.Equals(candidate.Scheme, directory.Scheme, StringComparison.Ordinal) ||
            !string.Equals(candidate.Host, directory.Host, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != directory.Port ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
            return false;

        var directoryPath = directory.AbsolutePath;
        var candidatePath = candidate.AbsolutePath;
        if (!candidatePath.StartsWith(directoryPath, StringComparison.Ordinal))
            return false;

        var fileName = candidatePath[directoryPath.Length..];
        return FileNamePattern.IsMatch(fileName);
    }

    /// <summary>
    /// Parses an artifact URL exactly as written. The canonical-form check rejects inputs whose
    /// normalization would silently change them, so the boundary check below sees the same URI the
    /// Registry document claimed.
    /// </summary>
    public static bool TryParseCanonicalAbsolute(string value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.AbsoluteUri, value, StringComparison.Ordinal))
        {
            uri = null!;
            return false;
        }
        uri = parsed;
        return true;
    }
}
