using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collection.Hub.Collectors.Packages;

public sealed record CollectorMarketplaceTarget(string OperatingSystem, string Architecture);

public sealed record CollectorCatalogItem(
    string PackageId,
    string DisplayName,
    string Summary,
    string Version,
    CollectorMarketplaceTarget Target);

public interface ICollectorPackageMarketplace
{
    ValueTask<IReadOnlyList<CollectorCatalogItem>> BrowseAsync(
        CancellationToken cancellationToken = default);

    ValueTask<CollectorPackageInstallation> InstallLatestAsync(
        string packageId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic Web Catalog and Package acquisition boundary. Callers select a PackageId returned by
/// <see cref="BrowseAsync"/>; release URLs are never accepted from callers. The module validates
/// Catalog, exact release metadata, artifact bytes, archive shape, and Package self-description
/// before publishing an Installation.
/// </summary>
public sealed class CollectorPackageMarketplace : ICollectorPackageMarketplace
{
    private const long MaximumCatalogBytes = 1_048_576;
    private const long MaximumReleaseBytes = 262_144;
    private const long MaximumArtifactBytes = 268_435_456;
    private const long MaximumExtractedBytes = 536_870_912;
    private const int MaximumArchiveEntries = 10_000;
    private readonly HttpClient _http;
    private readonly Uri _registryRoot;
    private readonly CollectorMarketplaceTarget _target;
    private readonly CollectorPackageInstallations _installations;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public CollectorPackageMarketplace(
        HttpClient http,
        Uri registryRoot,
        CollectorMarketplaceTarget target,
        CollectorPackageInstallations installations)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(registryRoot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(installations);
        if (!registryRoot.IsAbsoluteUri || registryRoot.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(registryRoot.UserInfo) ||
            !string.IsNullOrEmpty(registryRoot.Query) || !string.IsNullOrEmpty(registryRoot.Fragment))
            throw new ArgumentException("Collector registry root must be an absolute HTTPS URL.", nameof(registryRoot));
        if (string.IsNullOrWhiteSpace(target.OperatingSystem) || string.IsNullOrWhiteSpace(target.Architecture))
            throw new ArgumentException("Collector marketplace target must be complete.", nameof(target));
        _http = http;
        _registryRoot = new Uri(registryRoot.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _target = target;
        _installations = installations;
    }

    public async ValueTask<IReadOnlyList<CollectorCatalogItem>> BrowseAsync(
        CancellationToken cancellationToken = default)
    {
        var catalog = await FetchCatalogAsync(cancellationToken);
        return catalog.Packages
            .Select(entry => (Entry: entry, Latest: LatestForTarget(entry)))
            .Where(candidate => candidate.Latest is not null)
            .Select(candidate => new CollectorCatalogItem(
                candidate.Entry.PackageId,
                candidate.Entry.DisplayName,
                candidate.Entry.Summary,
                candidate.Latest!.Version,
                _target))
            .OrderBy(entry => entry.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<CollectorPackageInstallation> InstallLatestAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ValidatePackageId(packageId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await FetchCatalogAsync(cancellationToken);
            var entry = catalog.Packages.SingleOrDefault(candidate => candidate.PackageId == packageId)
                        ?? throw new KeyNotFoundException($"Collector Package '{packageId}' is not in the Catalog.");
            var latest = LatestForTarget(entry)
                         ?? throw new PackageValidationException(
                             $"Collector Package '{packageId}' has no latest release for the current target.");

            var releaseUri = ValidateRegistryUri(latest.ReleaseUrl, packageId, "release metadata");
            var release = await ReadJsonAsync<ReleaseDocument>(
                releaseUri,
                MaximumReleaseBytes,
                "Collector release metadata",
                cancellationToken);
            ValidateRelease(entry, latest, release);

            var artifactUri = ValidateRegistryUri(release.Artifact.Url, packageId, "artifact");
            var expectedArtifactUri = new Uri(releaseUri, release.Artifact.FileName);
            if (artifactUri != expectedArtifactUri)
                throw new PackageValidationException(
                    "Collector release artifact URL must be a sibling of its exact release metadata.");

            var temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                $"heartbeat-collector-marketplace-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                var archivePath = Path.Combine(temporaryRoot, "package.zip");
                await DownloadArtifactAsync(release, artifactUri, archivePath, cancellationToken);
                var packageDirectory = Path.Combine(temporaryRoot, "package");
                try { ExtractArchive(archivePath, packageDirectory); }
                catch (Exception exception) when (exception is
                    InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    throw new PackageValidationException("Collector Package artifact is not a valid zip archive.", exception);
                }
                var package = LocalCollectorPackage.Load(packageDirectory);
                ValidatePackage(entry, latest, package);
                return _installations.Install(packageDirectory);
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                    Directory.Delete(temporaryRoot, recursive: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CatalogDocument> FetchCatalogAsync(CancellationToken cancellationToken)
    {
        var catalog = await ReadJsonAsync<CatalogDocument>(
            new Uri(_registryRoot, "catalog.json"),
            MaximumCatalogBytes,
            "Collector Catalog",
            cancellationToken);
        if (catalog.SchemaVersion != 1 || catalog.Packages is null)
            throw new PackageValidationException("Collector Catalog has an unsupported schemaVersion or package list.");
        var duplicate = catalog.Packages.GroupBy(entry => entry.PackageId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new PackageValidationException(
                $"Collector Catalog contains duplicate PackageId '{duplicate.Key}'.");
        foreach (var entry in catalog.Packages)
            ValidateCatalogEntry(entry);
        return catalog;
    }

    private void ValidateCatalogEntry(CatalogEntry entry)
    {
        try { ValidatePackageId(entry.PackageId); }
        catch (ArgumentException exception)
        {
            throw new PackageValidationException("Collector Catalog contains an invalid PackageId.", exception);
        }
        RequireText(entry.DisplayName, "Catalog displayName", 80);
        RequireText(entry.Summary, "Catalog summary", 240);
        if (entry.Latest is null || entry.Latest.Count == 0)
            throw new PackageValidationException("Collector Catalog latest releases are missing.");
        var duplicateTarget = entry.Latest
            .Where(latest => latest.Target is not null)
            .GroupBy(latest => (latest.Target.Os, latest.Target.Arch))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget is not null)
            throw new PackageValidationException(
                $"Collector Catalog contains duplicate latest target '{duplicateTarget.Key.Os}/{duplicateTarget.Key.Arch}'.");
        foreach (var latest in entry.Latest)
        {
            RequireStableVersion(latest.Version, "Catalog latest version");
            if (latest.Target is null)
                throw new PackageValidationException("Collector Catalog latest target is missing.");
            RequireText(latest.Target.Os, "Catalog target os", 32);
            RequireText(latest.Target.Arch, "Catalog target arch", 32);
            _ = ValidateRegistryUri(latest.ReleaseUrl, entry.PackageId, "release metadata");
        }
    }

    private static void ValidateRelease(
        CatalogEntry entry,
        CatalogLatest latest,
        ReleaseDocument release)
    {
        if (release.SchemaVersion != 1 || release.PackageId != entry.PackageId ||
            release.Version != latest.Version ||
            release.Target?.Os != latest.Target.Os ||
            release.Target?.Arch != latest.Target.Arch || release.Artifact is null)
            throw new PackageValidationException(
                "Collector release metadata does not match the selected Catalog entry.");
        if (release.Artifact.Length is <= 0 or > MaximumArtifactBytes)
            throw new PackageValidationException("Collector release artifact length is outside the supported range.");
        if (!IsSha256(release.Artifact.Sha256))
            throw new PackageValidationException("Collector release artifact sha256 is invalid.");
        RequireText(release.Artifact.FileName, "Collector release artifact fileName", 200);
        if (release.Artifact.FileName != Path.GetFileName(release.Artifact.FileName) ||
            !release.Artifact.FileName.EndsWith(".zip", StringComparison.Ordinal))
            throw new PackageValidationException("Collector release artifact fileName must be a plain zip filename.");
    }

    private static void ValidatePackage(
        CatalogEntry entry,
        CatalogLatest latest,
        LocalCollectorPackage package)
    {
        if (package.Manifest.PackageId != entry.PackageId || package.Manifest.Version != latest.Version)
            throw new PackageValidationException("Downloaded Collector Package identity does not match the Catalog.");
        var presentation = package.Manifest.Presentation
                           ?? throw new PackageValidationException(
                               "Marketplace Collector Package must declare presentation.");
        _ = package.Manifest.DefaultInstance
            ?? throw new PackageValidationException(
                "Marketplace Collector Package must declare defaultInstance.");
        if (presentation.DisplayName != entry.DisplayName || presentation.Summary != entry.Summary)
            throw new PackageValidationException(
                "Downloaded Collector Package presentation does not match the Catalog.");
    }

    private async Task DownloadArtifactAsync(
        ReleaseDocument release,
        Uri artifactUri,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            artifactUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.RequestMessage?.RequestUri is { } artifactResponseUri && artifactResponseUri != artifactUri)
            throw new PackageValidationException("Collector artifact request redirected outside its exact URL.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"Collector artifact request returned {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != release.Artifact.Length)
            throw new PackageValidationException("Collector artifact Content-Length does not match release metadata.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            length += read;
            if (length > MaximumArtifactBytes || length > release.Artifact.Length)
                throw new PackageValidationException("Collector artifact exceeds its declared length.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (length != release.Artifact.Length ||
            "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Artifact.Sha256)
            throw new PackageValidationException("Collector artifact bytes do not match release metadata.");
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaximumArchiveEntries)
            throw new PackageValidationException("Collector Package archive has an invalid entry count.");
        if (archive.Entries.Sum(entry => entry.Length) > MaximumExtractedBytes)
            throw new PackageValidationException("Collector Package archive expands beyond the supported size.");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') ||
                entry.FullName.Split('/').Any(segment => segment is "" or "." or ".."))
                throw new PackageValidationException("Collector Package archive contains an unsafe path.");
            var unixMode = entry.ExternalAttributes >> 16;
            if ((unixMode & 0xF000) == 0xA000)
                throw new PackageValidationException("Collector Package archive must not contain symbolic links.");
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(root, StringComparison.Ordinal))
                throw new PackageValidationException("Collector Package archive entry escapes its root.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            if (!OperatingSystem.IsWindows() && (unixMode & 0x40) != 0)
            {
                var mode = File.GetUnixFileMode(path);
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
            }
        }
    }

    private async Task<T> ReadJsonAsync<T>(
        Uri uri,
        long maximumBytes,
        string description,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri is { } responseUri && responseUri != uri)
            throw new PackageValidationException($"{description} request redirected outside its exact URL.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"{description} request returned {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new PackageValidationException($"{description} is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > maximumBytes)
                throw new PackageValidationException($"{description} is too large.");
            buffer.Write(bytes, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions)
                   ?? throw new PackageValidationException($"{description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new PackageValidationException($"{description} is not valid schema v1 JSON.", exception);
        }
    }

    private Uri ValidateRegistryUri(string value, string packageId, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != _registryRoot.Host || uri.Port != _registryRoot.Port ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new PackageValidationException($"Collector {description} URL must use the Registry HTTPS origin.");
        var packagePrefix = new Uri(_registryRoot, $"packages/{packageId}/versions/").AbsolutePath;
        if (!uri.AbsolutePath.StartsWith(packagePrefix, StringComparison.Ordinal))
            throw new PackageValidationException($"Collector {description} URL is outside its Package namespace.");
        return uri;
    }

    private CatalogLatest? LatestForTarget(CatalogEntry entry) =>
        entry.Latest.SingleOrDefault(latest =>
            latest.Target.Os == _target.OperatingSystem &&
            latest.Target.Arch == _target.Architecture);

    private static void ValidatePackageId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            !char.IsAsciiLetterLower(value[0]) ||
            value.Split('.', '-').Any(segment => segment.Length == 0 ||
                segment.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character))))
            throw new ArgumentException("Collector PackageId is invalid.", nameof(value));
    }

    private static void RequireText(string? value, string description, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim())
            throw new PackageValidationException($"{description} is invalid.");
    }

    private static void RequireStableVersion(string? value, string description)
    {
        if (value is null)
            throw new PackageValidationException($"{description} must be a stable X.Y.Z SemVer.");
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part =>
                !int.TryParse(part, out _) || (part.Length > 1 && part[0] == '0')))
            throw new PackageValidationException($"{description} must be a stable X.Y.Z SemVer.");
    }

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(char.IsAsciiHexDigitLower);

    private sealed record CatalogDocument(int SchemaVersion, IReadOnlyList<CatalogEntry> Packages);
    private sealed record CatalogEntry(
        string PackageId,
        string DisplayName,
        string Summary,
        IReadOnlyList<CatalogLatest> Latest);
    private sealed record CatalogLatest(string Version, ReleaseTarget Target, string ReleaseUrl);
    private sealed record ReleaseDocument(
        int SchemaVersion,
        string PackageId,
        string Version,
        ReleaseTarget Target,
        ReleaseArtifact Artifact);
    private sealed record ReleaseTarget(string Os, string Arch);
    private sealed record ReleaseArtifact(
        string FileName,
        string Url,
        long Length,
        string Sha256);
}
