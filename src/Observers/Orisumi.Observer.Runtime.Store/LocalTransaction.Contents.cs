using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Observer.Runtime.Store;

public sealed partial class LocalTransaction
{
    internal IReadOnlyList<BinaryValue> Contents(VersionSubmission version) => BinaryValue.Collect(version,
        version.SchemaVersionId is { } schema ? Find(Resolve(schema))?.Version.Content as EntitySchema : null);

    internal Dictionary<string, byte[]> AcceptContents(JsonElement parameters)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!parameters.TryGetProperty("contents", out var items)) return result;
        foreach (var item in items.EnumerateArray())
        {
            var digest = item.GetProperty("digest").GetString()!;
            var data = item.GetProperty("data").GetBytesFromBase64();
            if (!BinaryValue.ValidDigest(digest) || BinaryValue.ComputeDigest(data) != digest)
                throw new ArgumentException("Content bytes do not match their digest.");
            result[digest] = data;
            Execute("INSERT OR IGNORE INTO local_contents(digest,data) VALUES($digest,$data)", ("$digest", digest), ("$data", data));
        }
        return result;
    }

    internal void ValidateContents(VersionSubmission version, IReadOnlyDictionary<string, byte[]> supplied)
    {
        foreach (var descriptor in Contents(version))
            if (!supplied.TryGetValue(descriptor.Digest, out var data) || data.LongLength != descriptor.Size)
                throw new ArgumentException("Every binary value requires matching bytes in this batch.");
    }

    public byte[]? ReadContent(string digest)
    {
        using var command = Command("SELECT data FROM local_contents WHERE digest=$digest", ("$digest", digest));
        return command.ExecuteScalar() as byte[];
    }

    internal bool ContentUploaded(string digest)
    {
        using var command = Command("SELECT available FROM uploaded_contents WHERE digest=$digest", ("$digest", digest));
        return command.ExecuteScalar() is long value && value == 1;
    }

    internal void SetContentUploaded(string digest, bool uploaded) => Execute(
        "INSERT INTO uploaded_contents(digest,available) VALUES($digest,$available) ON CONFLICT(digest) DO UPDATE SET available=excluded.available",
        ("$digest", digest), ("$available", uploaded ? 1 : 0));

    internal void PruneContents()
    {
        var needed = Versions().Where(version => version.State != SubmissionState.Saved)
            .SelectMany(version => Contents(version.Version)).Select(content => content.Digest).ToHashSet(StringComparer.Ordinal);
        var digests = new List<string>();
        using (var command = Command("SELECT digest FROM local_contents"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) digests.Add(reader.GetString(0));
        foreach (var digest in digests.Where(digest => !needed.Contains(digest)))
            Execute("DELETE FROM local_contents WHERE digest=$digest", ("$digest", digest));
    }
}
