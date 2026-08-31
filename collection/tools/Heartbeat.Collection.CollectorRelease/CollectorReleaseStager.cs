using System.Security.Cryptography;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.CollectorRelease;

/// <summary>What to stage: one built Collector Package, under one explicit tag.</summary>
public sealed record CollectorReleaseRequest(
    string Tag,
    string PackageDirectory,
    Uri RegistryBaseUri,
    string OutputDirectory);

/// <summary>
/// Turns a built Collector Package into the static Registry tree an operator can copy to the server:
/// <c>packages/{packageId}/versions/{version}/{artifact}</c> plus <c>packages/{packageId}/current.json</c>.
///
/// Length and SHA-256 are always computed from the bytes actually staged, never supplied by hand.
/// The staged tree is then re-read with the Runtime's own index reader and Package loader, so a
/// release that the Runtime could not consume fails here rather than on a user's machine.
///
/// An already published Version is never overwritten: staging refuses when the target version
/// directory holds different bytes, because a fix has to become a new tag.
/// </summary>
public static class CollectorReleaseStager
{
    public static CollectorReleaseResult Stage(CollectorReleaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CollectorReleaseTag.Parse(request.Tag) is not { } tag)
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.InvalidTag,
                $"Tag '{request.Tag}' is not 'collector-{{slug}}/v{{semver}}'.");
        if (!LocalCollectorPackage.IsValidSemVer(tag.Version))
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.InvalidTag,
                $"Tag '{request.Tag}' does not carry a valid SemVer version.");
        if (CollectorReleaseTarget.Find(tag.Slug) is not { } target)
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.UnknownReleaseTarget,
                $"No Collector publishes under slug '{tag.Slug}'.");

        LocalCollectorPackage package;
        try
        {
            package = LocalCollectorPackage.Load(request.PackageDirectory);
        }
        catch (PackageValidationException exception)
        {
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.PackageLoadFailed,
                $"'{request.PackageDirectory}' is not a loadable Collector Package: {exception.Message}");
        }

        if (!string.Equals(package.Manifest.PackageId, target.PackageId, StringComparison.Ordinal))
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.PackageIdMismatch,
                $"Package declares '{package.Manifest.PackageId}' but '{target.Slug}' releases '{target.PackageId}'.");
        if (!string.Equals(package.Manifest.Version, tag.Version, StringComparison.Ordinal))
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.VersionMismatch,
                $"Tag version '{tag.Version}' does not match Package manifest version '{package.Manifest.Version}'.");
        if (SelfContainedEntrypoint(package) is { } selfContained)
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.SelfContainedArtifact,
                $"Artifact '{selfContained}' is self-contained; this release publishes framework-dependent output.");

        var archive = CollectorPackageArchive.Pack(package.PackageDirectory);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(archive));
        var artifactUrl = new Uri(
            NormalizeBase(request.RegistryBaseUri),
            $"packages/{package.Manifest.PackageId}/versions/{package.Manifest.Version}/{target.ArtifactFileName}");
        var index = new CollectorRegistryIndex(
            CollectorRegistryIndexReader.SupportedSchemaVersion,
            package.Manifest.PackageId,
            package.Manifest.Version,
            new CollectorRegistryArtifact(artifactUrl, archive.LongLength, sha256));
        var indexBytes = CollectorRegistryIndexWriter.Write(index);

        // Refuse before touching the staging tree if the Runtime could not read what we would write.
        var preflight = CollectorRegistryIndexReader.Read(
            indexBytes,
            request.RegistryBaseUri,
            package.Manifest.PackageId);
        if (!preflight.IsSuccess)
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.IndexVerificationFailed,
                $"The index this release would publish is not readable: {preflight.Detail}",
                preflight.Reason);

        var packageRoot = Path.Combine(
            Path.GetFullPath(request.OutputDirectory),
            "packages",
            package.Manifest.PackageId);
        var versionDirectory = Path.Combine(packageRoot, "versions", package.Manifest.Version);
        var artifactPath = Path.Combine(versionDirectory, target.ArtifactFileName);
        var indexPath = Path.Combine(packageRoot, CollectorRegistryIndexReader.IndexFileName);

        if (File.Exists(artifactPath) && !File.ReadAllBytes(artifactPath).AsSpan().SequenceEqual(archive))
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.VersionAlreadyPublished,
                $"Version {package.Manifest.Version} is already published with different content at " +
                $"'{artifactPath}'. Published versions are immutable: publish a new tag instead.");

        // Artifact first, index last: the index must never name a file that is not readable yet.
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllBytes(artifactPath, archive);
        File.WriteAllBytes(indexPath, indexBytes);

        var verification = Verify(request, target, package, index, indexPath, artifactPath);
        if (verification is not null)
            return verification;

        return new CollectorReleaseResult(
            true,
            null,
            null,
            $"Staged {tag} for {package.Manifest.PackageId}.",
            index,
            indexPath,
            artifactPath,
            package.PackageContentHash,
            [
                $"tag: {tag}",
                $"packageId: {package.Manifest.PackageId}",
                $"version: {package.Manifest.Version}",
                $"artifact: {artifactPath}",
                $"artifact url: {index.Artifact.Url}",
                $"artifact length: {index.Artifact.Length}",
                $"artifact sha256: {index.Artifact.Sha256}",
                $"package content hash: {package.PackageContentHash}",
                $"index: {indexPath}",
                "verified: index re-read, length and SHA-256 recomputed, archive reloaded by the Package loader"
            ]);
    }

    /// <summary>
    /// Re-reads the staged tree the way the Runtime would: parse the published index, recompute the
    /// artifact length and hash from disk, and unpack the archive back into a loadable Collector
    /// Package with the same identity and content hash.
    /// </summary>
    private static CollectorReleaseResult? Verify(
        CollectorReleaseRequest request,
        CollectorReleaseTarget target,
        LocalCollectorPackage source,
        CollectorRegistryIndex expected,
        string indexPath,
        string artifactPath)
    {
        var published = CollectorRegistryIndexReader.Read(
            File.ReadAllBytes(indexPath),
            request.RegistryBaseUri,
            source.Manifest.PackageId);
        if (!published.IsSuccess)
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.IndexVerificationFailed,
                $"The staged index is not readable: {published.Detail}",
                published.Reason);
        if (published.Require() != expected)
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.IndexVerificationFailed,
                "The staged index does not match the release that produced it.");

        var staged = File.ReadAllBytes(artifactPath);
        if (staged.LongLength != expected.Artifact.Length)
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.IndexVerificationFailed,
                $"Staged artifact is {staged.LongLength} bytes, index declares {expected.Artifact.Length}.");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(staged));
        if (!string.Equals(sha256, expected.Artifact.Sha256, StringComparison.Ordinal))
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.IndexVerificationFailed,
                $"Staged artifact hashes to {sha256}, index declares {expected.Artifact.Sha256}.");

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"heartbeat-collector-release-verify-{Guid.NewGuid():N}");
        try
        {
            CollectorPackageArchive.Unpack(staged, scratch);
            var reloaded = LocalCollectorPackage.Load(scratch);
            if (!string.Equals(reloaded.PackageContentHash, source.PackageContentHash, StringComparison.Ordinal) ||
                !string.Equals(reloaded.Manifest.PackageId, expected.PackageId, StringComparison.Ordinal) ||
                !string.Equals(reloaded.Manifest.Version, expected.Version, StringComparison.Ordinal))
                return CollectorReleaseResult.Refuse(
                    CollectorReleaseFailure.IndexVerificationFailed,
                    "The staged archive does not reload as the Collector Package it was built from.");
            if (!reloaded.Artifacts.Any(artifact => artifact.ArtifactId.StartsWith(target.Slug, StringComparison.Ordinal)))
                return CollectorReleaseResult.Refuse(
                    CollectorReleaseFailure.IndexVerificationFailed,
                    $"The staged archive carries no '{target.Slug}' artifact.");
        }
        catch (Exception exception) when (exception is PackageValidationException or InvalidOperationException)
        {
            return CollectorReleaseResult.Refuse(
                CollectorReleaseFailure.IndexVerificationFailed,
                $"The staged archive is not a loadable Collector Package: {exception.Message}");
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
        return null;
    }

    /// <summary>
    /// Returns the entrypoint that ships its own runtime, or <c>null</c> when every artifact is
    /// framework-dependent. A self-contained build would be a different delivery decision.
    /// </summary>
    private static string? SelfContainedEntrypoint(LocalCollectorPackage package)
    {
        foreach (var artifact in package.Artifacts)
        {
            // Only the Windows ".exe" suffix is an extension here; a Unix apphost is named after the
            // assembly, dots included.
            var entrypoint = artifact.Entrypoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? artifact.Entrypoint[..^4]
                : artifact.Entrypoint;
            var runtimeConfig = Path.Combine(package.PackageDirectory, entrypoint + ".runtimeconfig.json");
            if (!File.Exists(runtimeConfig))
                continue;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(runtimeConfig));
            if (document.RootElement.TryGetProperty("runtimeOptions", out var options) &&
                options.TryGetProperty("includedFrameworks", out _))
                return artifact.Entrypoint;
        }
        return null;
    }

    private static Uri NormalizeBase(Uri registryBaseUri) =>
        registryBaseUri.AbsolutePath.EndsWith('/')
            ? registryBaseUri
            : new Uri(registryBaseUri.GetLeftPart(UriPartial.Path) + "/", UriKind.Absolute);
}
