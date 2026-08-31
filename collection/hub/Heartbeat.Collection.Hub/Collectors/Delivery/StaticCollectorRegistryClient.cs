using System.Net;
using System.Security.Cryptography;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// Reads a static Official Collector Package Registry over HTTP(S): fetch <c>current.json</c>, then
/// download the exact artifact it names and accept it only when the bytes match the declared length
/// and SHA-256.
///
/// It stops there on purpose. Unpacking, version directories and completion markers are Collector
/// Installation concerns and are not part of reading the Registry.
/// </summary>
public sealed class StaticCollectorRegistryClient(HttpClient httpClient, Uri registryBaseUri)
{
    private const int MaxRedirects = 4;

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public Uri RegistryBaseUri { get; } = registryBaseUri ?? throw new ArgumentNullException(nameof(registryBaseUri));

    public async Task<CollectorRegistryResult<CollectorRegistryIndex>> GetCurrentAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (!CollectorRegistryBoundary.TryNormalizeBase(RegistryBaseUri, out var baseUri, out var detail))
            return CollectorRegistryResult<CollectorRegistryIndex>.Failure(
                CollectorRegistryFailureReason.InvalidRegistryBaseUri,
                detail);

        var directory = CollectorRegistryBoundary.PackageDirectory(baseUri, packageId);
        var indexUri = new Uri(directory, CollectorRegistryIndexReader.IndexFileName);

        var fetched = await FetchAsync(indexUri, directory, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess)
            return CollectorRegistryResult<CollectorRegistryIndex>.Failure(fetched);

        using var response = fetched.Require();
        byte[] body;
        try
        {
            body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return CollectorRegistryResult<CollectorRegistryIndex>.Failure(
                CollectorRegistryFailureReason.RequestFailed,
                exception.Message);
        }

        return CollectorRegistryIndexReader.Read(body, RegistryBaseUri, packageId);
    }

    /// <summary>
    /// Streams the artifact named by <paramref name="index" /> into <paramref name="destination" />
    /// and returns the verified artifact. The bytes are only acceptable when both the length and the
    /// SHA-256 match; a short, long or altered body is a structured failure.
    /// </summary>
    public async Task<CollectorRegistryResult<CollectorRegistryArtifact>> DownloadArtifactAsync(
        CollectorRegistryIndex index,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(destination);

        if (!CollectorRegistryBoundary.TryNormalizeBase(RegistryBaseUri, out var baseUri, out var detail))
            return CollectorRegistryResult<CollectorRegistryArtifact>.Failure(
                CollectorRegistryFailureReason.InvalidRegistryBaseUri,
                detail);

        var directory = CollectorRegistryBoundary.VersionDirectory(baseUri, index.PackageId, index.Version);
        if (!CollectorRegistryBoundary.IsFileWithin(directory, index.Artifact.Url))
            return CollectorRegistryResult<CollectorRegistryArtifact>.Failure(
                CollectorRegistryFailureReason.ArtifactUrlOutsideRegistry,
                $"Artifact URL '{index.Artifact.Url}' is not a file inside '{directory}'.");

        var fetched = await FetchAsync(index.Artifact.Url, directory, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess)
            return CollectorRegistryResult<CollectorRegistryArtifact>.Failure(fetched);

        using var response = fetched.Require();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var total = 0L;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
                if (total > index.Artifact.Length)
                    return CollectorRegistryResult<CollectorRegistryArtifact>.Failure(
                        CollectorRegistryFailureReason.ArtifactLengthMismatch,
                        $"Artifact is longer than the declared {index.Artifact.Length} bytes.");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException exception)
        {
            return CollectorRegistryResult<CollectorRegistryArtifact>.Failure(
                CollectorRegistryFailureReason.RequestFailed,
                exception.Message);
        }

        if (total != index.Artifact.Length)
            return CollectorRegistryResult<CollectorRegistryArtifact>.Failure(
                CollectorRegistryFailureReason.ArtifactLengthMismatch,
                $"Artifact is {total} bytes, expected {index.Artifact.Length}.");

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(index.Artifact.Sha256)))
            return CollectorRegistryResult<CollectorRegistryArtifact>.Failure(
                CollectorRegistryFailureReason.ArtifactHashMismatch,
                $"Artifact SHA-256 is {actual}, expected {index.Artifact.Sha256}.");

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return CollectorRegistryResult<CollectorRegistryArtifact>.Success(index.Artifact);
    }

    /// <summary>
    /// Issues the request and re-applies <paramref name="directory" /> to every hop. Redirects are
    /// followed here rather than by the handler so a 302 cannot smuggle the download outside the
    /// Registry; when the caller's handler follows redirects itself, the final request URI is
    /// re-checked for the same reason.
    /// </summary>
    private async Task<CollectorRegistryResult<HttpResponseMessage>> FetchAsync(
        Uri requestUri,
        Uri directory,
        CancellationToken cancellationToken)
    {
        var current = requestUri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                return CollectorRegistryResult<HttpResponseMessage>.Failure(
                    CollectorRegistryFailureReason.RequestFailed,
                    exception.Message);
            }

            var landed = response.RequestMessage?.RequestUri ?? current;
            if (!CollectorRegistryBoundary.IsFileWithin(directory, landed))
            {
                response.Dispose();
                return CollectorRegistryResult<HttpResponseMessage>.Failure(
                    CollectorRegistryFailureReason.RedirectOutsideRegistry,
                    $"Request landed on '{landed}', which is outside '{directory}'.");
            }

            if (!IsRedirect(response.StatusCode))
            {
                if (response.IsSuccessStatusCode)
                    return CollectorRegistryResult<HttpResponseMessage>.Success(response);

                var status = response.StatusCode;
                response.Dispose();
                return CollectorRegistryResult<HttpResponseMessage>.Failure(
                    CollectorRegistryFailureReason.RequestFailed,
                    $"Registry answered {(int)status} for '{current}'.");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                return CollectorRegistryResult<HttpResponseMessage>.Failure(
                    CollectorRegistryFailureReason.RequestFailed,
                    $"Registry redirected '{current}' without a Location header.");

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!CollectorRegistryBoundary.IsFileWithin(directory, next))
                return CollectorRegistryResult<HttpResponseMessage>.Failure(
                    CollectorRegistryFailureReason.RedirectOutsideRegistry,
                    $"Registry redirected '{current}' to '{next}', which is outside '{directory}'.");
            current = next;
        }

        return CollectorRegistryResult<HttpResponseMessage>.Failure(
            CollectorRegistryFailureReason.TooManyRedirects,
            $"Registry redirected '{requestUri}' more than {MaxRedirects} times.");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
