using System.Collections.Immutable;

namespace Orisumi.Core;

public sealed record InputSet : VersionContent
{
    public InputSet(IEnumerable<VersionId> members) => Members = members.ToImmutableArray();
    public ImmutableArray<VersionId> Members { get; }
    public override string Category => "input_set";
}
