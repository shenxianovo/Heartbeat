using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Heartbeat.Observers.Sources.Local;

/// <summary>Read-only bounded snapshot of HEAD, its ordered parents, and current refs. Never runs hooks or remote commands.</summary>
public sealed class GitSource : IRecordSource
{
    public string Name => "git";
    private static readonly SourceSchema CommitSchema = new(Guid.Parse("019a1280-0000-7000-8000-000000000010"), "git-source-commits", "Git commit material",
        SourceFields.WithIdentity(new { objectId = SourceFields.String(), treeObjectId = SourceFields.String(), authorEpoch = SourceFields.Integer(),
            authorOffset = SourceFields.String(), committerEpoch = SourceFields.Integer(), committerOffset = SourceFields.String(),
            encoding = SourceFields.String(true), rawCommitBase64 = SourceFields.String(), parentCount = SourceFields.Integer() }));
    private static readonly SourceSchema ParentSchema = new(Guid.Parse("019a1280-0000-7000-8000-000000000011"), "git-source-parents", "Ordered Git parent occurrence",
        SourceFields.WithIdentity(new { position = SourceFields.Integer(), commitId = SourceFields.Reference(), parentId = SourceFields.Reference() }));
    private static readonly SourceSchema RefSchema = new(Guid.Parse("019a1280-0000-7000-8000-000000000012"), "git-source-refs", "Observed Git reference",
        SourceFields.WithIdentity(new { refName = SourceFields.String(), objectId = SourceFields.String(), objectType = SourceFields.String(), targetId = SourceFields.Reference() }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(context.Input);
        async Task<byte[]> Git(params string[] args) => await SourceProcess.RunAsync("git", ["--no-pager", "-C", root, .. args], cancellationToken);
        var oid = SourceProcess.StrictText(await Git("rev-parse", "--verify", "HEAD^{commit}")).Trim();
        var raw = await Git("cat-file", "commit", oid);
        // Parse ASCII commit headers only; arbitrary message/signature bytes remain exact in rawCommitBase64.
        var headerEnd = raw.AsSpan().IndexOf("\n\n"u8);
        if (headerEnd < 0) throw new InvalidDataException("Git commit has no header boundary.");
        var headers = Encoding.UTF8.GetString(raw, 0, headerEnd).Split('\n');
        string Header(string name) => headers.FirstOrDefault(h => h.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..]
            ?? throw new InvalidDataException($"Git commit is missing {name}.");
        var (authorEpoch, authorOffset) = ActorTime(Header("author"));
        var (committerEpoch, committerOffset) = ActorTime(Header("committer"));
        var parents = headers.Where(h => h.StartsWith("parent ", StringComparison.Ordinal)).Select(h => h[7..]).ToArray();
        var instant = DateTimeOffset.FromUnixTimeSeconds(committerEpoch);
        yield return new("git-commit", oid, oid, CommitSchema,
            SourceFields.Of(new { objectId = oid, treeObjectId = Header("tree"), authorEpoch, authorOffset, committerEpoch, committerOffset,
                encoding = headers.FirstOrDefault(h => h.StartsWith("encoding ", StringComparison.Ordinal))?[9..], rawCommitBase64 = Convert.ToBase64String(raw), parentCount = parents.Length }),
            new Dictionary<string, SourceReference>(), instant, instant);
        for (var i = 0; i < parents.Length; i++)
            yield return new("git-parent-occurrence", $"{oid}:{i}", $"{oid}:{i}", ParentSchema, SourceFields.Of(new { position = i }),
                new Dictionary<string, SourceReference> { ["commitId"] = new("git-commit", oid), ["parentId"] = new("git-commit", parents[i]) }, instant, instant);
        var refs = SourceProcess.StrictText(await Git("for-each-ref", "--format=%(refname)%00%(objectname)%00%(objecttype)"));
        foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = line.Split('\0');
            if (parts.Length != 3) throw new InvalidDataException("Unexpected Git ref record.");
            yield return new("git-ref", parts[0], context.CaptureKey + ":" + parts[0], RefSchema,
                SourceFields.Of(new { refName = parts[0], objectId = parts[1], objectType = parts[2] }),
                new Dictionary<string, SourceReference> { ["targetId"] = new("git-" + parts[2], parts[1]) }, context.CapturedAt, context.CapturedAt);
        }
    }

    private static (long Epoch, string Offset) ActorTime(string value)
    {
        var parts = value.Split(' ');
        if (parts.Length < 3 || !long.TryParse(parts[^2], CultureInfo.InvariantCulture, out var epoch))
            throw new InvalidDataException("Git actor time is missing or invalid.");
        return (epoch, parts[^1]);
    }
}
