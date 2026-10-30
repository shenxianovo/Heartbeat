using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed class ContentService(IEntityStore store, TimeProvider timeProvider)
{
    public Task UploadAsync(string digest, byte[] data, CancellationToken token = default)
    {
        VersionRules.Require(BinaryValue.ValidDigest(digest) && BinaryValue.ComputeDigest(data) == digest,
            "BINARY-1", "The uploaded bytes do not match the digest.");
        return store.ExecuteAsync(async (transaction, cancellationToken) =>
        {
            await transaction.UploadContentAsync(digest, data, timeProvider.GetUtcNow(), cancellationToken);
            return true;
        }, token);
    }

    public Task<byte[]?> ReadAsync(string digest, CancellationToken token = default) =>
        store.ExecuteAsync((transaction, cancellationToken) => transaction.ReadContentAsync(digest, cancellationToken), token);

    public Task PruneAsync(CancellationToken token = default) => store.ExecuteAsync(async (transaction, cancellationToken) =>
    {
        await transaction.PruneContentsAsync(timeProvider.GetUtcNow().AddHours(-24), cancellationToken);
        return true;
    }, token);
}
