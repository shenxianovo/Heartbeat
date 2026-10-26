namespace Orisumi.Core;

public abstract record VersionContent
{
    private protected VersionContent() { }
    public abstract string Category { get; }
}
