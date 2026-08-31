using System.Collections.Concurrent;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>The last structured failure of an installation attempt, kept for later display.</summary>
public sealed record CollectorInstallationFailure(
    string PackageId,
    CollectorPackageReference? Reference,
    CollectorRegistryFailureReason Reason,
    string Detail);

/// <summary>
/// Installs one exact Registry candidate: read the index, download the artifact, verify its length
/// and SHA-256, unpack it safely into that candidate's own directory, re-verify the content through
/// the existing Collector Package loader, and only then write the completion marker.
///
/// It stops there. Approval, enablement and Activation are other modules' business, and nothing here
/// touches Collector Desired State, Runtime State or a Last-Known-Good record: a failed candidate can
/// therefore never demote a working one.
///
/// Failure never retries by itself. One call is one attempt; the structured reason is returned and
/// remembered as <see cref="LastFailure" /> so a later manual CheckNow or an API can show why, which
/// is exactly the manual-gate behaviour ADR-047 asks for.
/// </summary>
public sealed class CollectorPackageInstaller
{
    private const string DownloadFileName = "artifact.zip";

    private readonly StaticCollectorRegistryClient _registry;
    private readonly CollectorInstallationStore _installations;
    private readonly CollectorPackageArchiveLimits _limits;
    private readonly ConcurrentDictionary<string, CollectorInstallationFailure> _lastFailures =
        new(StringComparer.Ordinal);

    public CollectorPackageInstaller(
        StaticCollectorRegistryClient registry,
        CollectorInstallationStore installations,
        CollectorPackageArchiveLimits? limits = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _installations = installations ?? throw new ArgumentNullException(nameof(installations));
        _limits = limits ?? CollectorPackageArchiveLimits.Default;
    }

    /// <summary>
    /// The last failure recorded for <paramref name="packageId" />, or <c>null</c> when the most
    /// recent attempt succeeded. It is in-memory state about attempts, not a second lifecycle
    /// authority; nothing reads it to decide what is installed.
    /// </summary>
    public CollectorInstallationFailure? LastFailure(string packageId) =>
        _lastFailures.TryGetValue(packageId, out var failure) ? failure : null;

    /// <summary>Reads the Registry's current candidate for <paramref name="packageId" /> and installs it.</summary>
    public async Task<CollectorRegistryResult<CollectorInstallation>> InstallCurrentAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        CollectorRegistryResult<CollectorRegistryIndex> index;
        try
        {
            index = await _registry.GetCurrentAsync(packageId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Record(
                packageId,
                null,
                CollectorRegistryFailureReason.Cancelled,
                $"Reading the Registry index for '{packageId}' was cancelled.");
        }
        if (!index.IsSuccess)
            return Record(packageId, null, index.Reason!.Value, index.Detail!);

        return await InstallAsync(index.Require(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs exactly the candidate named by <paramref name="index" />. Re-running it for an
    /// already installed candidate is idempotent and does not download anything.
    /// </summary>
    public async Task<CollectorRegistryResult<CollectorInstallation>> InstallAsync(
        CollectorRegistryIndex index,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        var reference = CollectorPackageReference.FromIndex(index);
        var validated = reference.Validated();
        if (!validated.IsSuccess)
            return Record(index.PackageId, null, validated.Reason!.Value, validated.Detail!);

        var installed = _installations.OpenInstallation(reference);
        if (installed.IsSuccess)
        {
            _lastFailures.TryRemove(reference.PackageId, out _);
            return installed;
        }

        var prepared = _installations.CreatePendingContent();
        if (!prepared.IsSuccess)
            return Record(reference.PackageId, reference, prepared.Reason!.Value, prepared.Detail!);
        var content = prepared.Require();
        try
        {
            var result = await PrepareAsync(index, reference, content, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
                return Record(reference.PackageId, reference, result.Reason!.Value, result.Detail!);

            var published = _installations.Publish(content, reference);
            if (!published.IsSuccess)
                return Record(reference.PackageId, reference, published.Reason!.Value, published.Detail!);
            _lastFailures.TryRemove(reference.PackageId, out _);
            return published;
        }
        finally
        {
            CollectorInstallationStore.DiscardPendingContent(content);
        }
    }

    /// <summary>
    /// Downloads, unpacks and verifies into the attempt-private directory, finishing by writing the
    /// completion marker. Everything this touches is still invisible to
    /// <see cref="CollectorInstallationStore.OpenInstallation" />, so an abandoned attempt cannot be
    /// mistaken for a Collector Installation.
    /// </summary>
    private async Task<CollectorRegistryResult<CollectorPackageReference>> PrepareAsync(
        CollectorRegistryIndex index,
        CollectorPackageReference reference,
        string content,
        CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(Path.GetDirectoryName(content)!, DownloadFileName);
        try
        {
            await using (var file = new FileStream(
                             archivePath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None))
            {
                var downloaded = await _registry
                    .DownloadArtifactAsync(index, file, cancellationToken)
                    .ConfigureAwait(false);
                if (!downloaded.IsSuccess)
                    return Failure(downloaded.Reason!.Value, downloaded.Detail!);
            }

            using (var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var extracted = CollectorPackageArchiveExtractor.Extract(
                    archive,
                    content,
                    _limits,
                    cancellationToken);
                if (!extracted.IsSuccess)
                    return Failure(extracted.Reason!.Value, extracted.Detail!);
            }

            LocalCollectorPackage package;
            try
            {
                package = LocalCollectorPackage.Load(content);
            }
            catch (PackageValidationException exception)
            {
                return Failure(CollectorRegistryFailureReason.PackageValidationFailed, exception.Message);
            }
            if (!string.Equals(package.Manifest.PackageId, reference.PackageId, StringComparison.Ordinal) ||
                !string.Equals(package.Manifest.Version, reference.Version, StringComparison.Ordinal))
                return Failure(
                    CollectorRegistryFailureReason.PackageManifestMismatch,
                    $"The downloaded Package declares " +
                    $"{package.Manifest.PackageId}@{package.Manifest.Version}, not {reference}.");

            // Last write of the attempt: after this the prepared directory is admissible, and
            // publishing is a single rename of an already complete directory.
            await File.WriteAllBytesAsync(
                Path.Combine(content, CollectorInstallationMarker.FileName),
                CollectorInstallationMarker.Write(new CollectorInstallationMarker(
                    CollectorInstallationMarker.CurrentSchemaVersion,
                    reference.PackageId,
                    reference.Version,
                    reference.ArtifactSha256,
                    package.PackageContentHash)),
                cancellationToken).ConfigureAwait(false);
            return CollectorRegistryResult<CollectorPackageReference>.Success(reference);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                CollectorRegistryFailureReason.Cancelled,
                $"Installing {reference} was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(CollectorRegistryFailureReason.InstallationStorageFailed, exception.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The download stays inside the attempt directory, which is removed either way.
            }
        }
    }

    private static CollectorRegistryResult<CollectorPackageReference> Failure(
        CollectorRegistryFailureReason reason,
        string detail) =>
        CollectorRegistryResult<CollectorPackageReference>.Failure(reason, detail);

    private CollectorRegistryResult<CollectorInstallation> Record(
        string packageId,
        CollectorPackageReference? reference,
        CollectorRegistryFailureReason reason,
        string detail)
    {
        _lastFailures[packageId] = new CollectorInstallationFailure(packageId, reference, reason, detail);
        return CollectorRegistryResult<CollectorInstallation>.Failure(reason, detail);
    }
}
