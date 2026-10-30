using System.Net;
using Orisumi.Core;

namespace Orisumi.Observer.Runtime.Store;

public sealed partial class ObserverStore
{
    private async Task<bool> UploadContentsAsync(IReadOnlyList<LocalVersion> batch, CancellationToken token)
    {
        var digests = Execute(local => batch.SelectMany(version => local.Contents(version.Version))
            .Select(content => content.Digest).Distinct().Where(digest => !local.ContentUploaded(digest)).ToArray());
        foreach (var digest in digests)
        {
            var data = Execute(local => local.ReadContent(digest)) ?? throw new InvalidOperationException("Pending content bytes are missing.");
            using var body = new ByteArrayContent(data);
            body.Headers.ContentType = new("application/octet-stream");
            using var response = await _http.PutAsync("contents/" + digest, body, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized || (int)response.StatusCode >= 500) return false;
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                Fail($"Content upload failed: HTTP {(int)response.StatusCode}.");
                return false;
            }
            Execute(local => local.SetContentUploaded(digest, true));
        }
        return true;
    }
}
