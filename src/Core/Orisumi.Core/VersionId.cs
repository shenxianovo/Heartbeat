namespace Orisumi.Core;

public readonly record struct VersionId(Guid Value)
{
    public static VersionId New() => new(Guid.CreateVersion7());
}
