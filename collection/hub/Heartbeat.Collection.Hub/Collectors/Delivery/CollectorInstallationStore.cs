using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// Owns the local layout of Collector Installations and answers the one question that decides
/// whether the Runtime may use a directory at all.
///
/// The layout isolates every exact candidate: <c>&lt;state&gt;/collector-packages/packages/{packageId}/
/// {version}/{artifactSha256}/</c>. Two builds of the same declared Version therefore never share a
/// directory and cannot impersonate one another. The root is derived from the Hub state directory the
/// host already owns — the same directory that holds <c>collector-runtime.json</c> and
/// <c>collector-secrets</c> — so this class introduces no second configuration authority.
///
/// <see cref="OpenInstallation" /> is the single admission function. It requires all three of: the
/// completion marker exists, the marker still names the requested exact candidate, and the content
/// still loads through <see cref="LocalCollectorPackage" /> with the identity the marker recorded. A
/// directory failing any of them is not a Collector Installation, cannot be started or approved, and
/// may be replaced by the next attempt at the same candidate.
///
/// Publishing is a rename of a fully prepared directory, deliberately not an atomic install
/// transaction: there is no journal, no fsync barrier and no two-phase commit (out of scope per
/// ADR-047). The marker is the last thing written into the prepared directory, so a torn state is
/// always "not an Installation" rather than a half-admitted one.
/// </summary>
public sealed class CollectorInstallationStore
{
    /// <summary>Name of the delivery subtree inside the Hub state directory.</summary>
    public const string DeliveryDirectoryName = "collector-packages";

    private const string PackagesDirectoryName = "packages";
    private const string PendingDirectoryName = "pending";
    private const string PendingContentDirectoryName = "content";

    public CollectorInstallationStore(string hubStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hubStateDirectory);
        HubStateDirectory = Path.GetFullPath(hubStateDirectory);
        InstallRoot = Path.Combine(HubStateDirectory, DeliveryDirectoryName);
    }

    /// <summary>The Hub state directory this store hangs off, as chosen by the host.</summary>
    public string HubStateDirectory { get; }

    /// <summary>The delivery subtree; nothing outside it is written by installation.</summary>
    public string InstallRoot { get; }

    /// <summary>Root of the published, version- and content-isolated installation directories.</summary>
    public string PackagesRoot => Path.Combine(InstallRoot, PackagesDirectoryName);

    /// <summary>Root of the in-progress directories that are not yet, and may never become, Installations.</summary>
    public string PendingRoot => Path.Combine(InstallRoot, PendingDirectoryName);

    /// <summary>
    /// The directory that owns <paramref name="reference" />. Every part of the path comes from an
    /// already validated candidate, so no Registry-supplied text can traverse out of the store.
    /// </summary>
    public string DirectoryFor(CollectorPackageReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.IsWellFormed)
            throw new ArgumentException(
                $"Collector Package candidate '{reference}' is not well formed.",
                nameof(reference));
        return Path.Combine(
            PackagesRoot,
            reference.PackageId,
            reference.Version,
            reference.ArtifactSha256);
    }

    /// <summary>
    /// The only function that decides whether a local directory is a Collector Installation for
    /// exactly <paramref name="reference" />. Callers must not infer installation from a directory
    /// existing, from a marker alone, or from a successful Package load alone.
    /// </summary>
    public CollectorRegistryResult<CollectorInstallation> OpenInstallation(CollectorPackageReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var validated = reference.Validated();
        if (!validated.IsSuccess)
            return CollectorRegistryResult<CollectorInstallation>.Failure(validated);

        var directory = DirectoryFor(reference);
        var markerPath = Path.Combine(directory, CollectorInstallationMarker.FileName);
        byte[] markerBytes;
        try
        {
            if (!File.Exists(markerPath))
                return Fail(
                    CollectorRegistryFailureReason.InstallationMarkerMissing,
                    $"'{directory}' carries no completion marker, so it is not a Collector Installation.");
            markerBytes = File.ReadAllBytes(markerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(CollectorRegistryFailureReason.InstallationStorageFailed, exception.Message);
        }

        var marker = CollectorInstallationMarker.Read(markerBytes);
        if (marker is null)
            return Fail(
                CollectorRegistryFailureReason.InstallationMarkerMismatch,
                $"The completion marker in '{directory}' is not a readable marker document.");
        if (!marker.Completes(reference))
            return Fail(
                CollectorRegistryFailureReason.InstallationMarkerMismatch,
                $"The completion marker in '{directory}' completes " +
                $"{marker.PackageId}@{marker.Version} ({marker.ArtifactSha256}), not {reference}.");

        LocalCollectorPackage package;
        try
        {
            package = LocalCollectorPackage.Load(directory);
        }
        catch (PackageValidationException exception)
        {
            return Fail(CollectorRegistryFailureReason.PackageValidationFailed, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(CollectorRegistryFailureReason.InstallationStorageFailed, exception.Message);
        }

        if (!string.Equals(package.Manifest.PackageId, reference.PackageId, StringComparison.Ordinal) ||
            !string.Equals(package.Manifest.Version, reference.Version, StringComparison.Ordinal))
            return Fail(
                CollectorRegistryFailureReason.PackageManifestMismatch,
                $"'{directory}' holds {package.Manifest.PackageId}@{package.Manifest.Version}, not {reference}.");
        if (!string.Equals(package.PackageContentHash, marker.PackageContentHash, StringComparison.Ordinal))
            return Fail(
                CollectorRegistryFailureReason.InstallationMarkerMismatch,
                $"'{directory}' holds Package content hash {package.PackageContentHash}, " +
                $"but its completion marker recorded {marker.PackageContentHash}.");

        return CollectorRegistryResult<CollectorInstallation>.Success(
            new CollectorInstallation(reference, package));
    }

    /// <summary>
    /// Creates a fresh, attempt-private directory to unpack into. It is unique per attempt so two
    /// concurrent installations of the same candidate cannot write into each other's content.
    /// </summary>
    internal CollectorRegistryResult<string> CreatePendingContent()
    {
        try
        {
            var attempt = Path.Combine(PendingRoot, Guid.NewGuid().ToString("N"));
            var content = Path.Combine(attempt, PendingContentDirectoryName);
            Directory.CreateDirectory(content);
            return CollectorRegistryResult<string>.Success(content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CollectorRegistryResult<string>.Failure(
                CollectorRegistryFailureReason.InstallationStorageFailed,
                exception.Message);
        }
    }

    /// <summary>Drops an attempt's directory. A discarded attempt was never an Installation.</summary>
    internal static void DiscardPendingContent(string pendingContent)
    {
        var attempt = Path.GetDirectoryName(Path.GetFullPath(pendingContent));
        if (attempt is null)
            return;
        try
        {
            if (Directory.Exists(attempt))
                Directory.Delete(attempt, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing a temporary directory must not turn into a failed installation; it is not an
            // Installation either way, and the next attempt uses a new one.
        }
    }

    /// <summary>
    /// Publishes a prepared directory — content plus completion marker — as the Installation for
    /// <paramref name="reference" />, then re-asks <see cref="OpenInstallation" /> so the caller can
    /// only ever receive an admitted Installation.
    ///
    /// A directory already sitting in the way is either a real Installation for the same exact
    /// candidate (a concurrent attempt won, and its result is returned) or an unfinished leftover
    /// with the same identity, which is replaced.
    /// </summary>
    internal CollectorRegistryResult<CollectorInstallation> Publish(
        string preparedContent,
        CollectorPackageReference reference)
    {
        var target = DirectoryFor(reference);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(preparedContent, target);
                return OpenInstallation(reference);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(target))
                    return Fail(CollectorRegistryFailureReason.InstallationStorageFailed, exception.Message);

                var existing = OpenInstallation(reference);
                if (existing.IsSuccess)
                    return existing;
                try
                {
                    Directory.Delete(target, recursive: true);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    return Fail(CollectorRegistryFailureReason.InstallationStorageFailed, cleanup.Message);
                }
            }
        }
        return Fail(
            CollectorRegistryFailureReason.InstallationStorageFailed,
            $"'{target}' could not be published after repeated attempts.");
    }

    private static CollectorRegistryResult<CollectorInstallation> Fail(
        CollectorRegistryFailureReason reason,
        string detail) =>
        CollectorRegistryResult<CollectorInstallation>.Failure(reason, detail);
}
