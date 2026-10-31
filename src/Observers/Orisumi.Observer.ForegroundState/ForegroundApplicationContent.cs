namespace Orisumi.Observers.ForegroundState;

internal sealed class ForegroundApplicationContent
{
    public required string? Name { get; init; }
    public Orisumi.Observer.Sdk.BinaryContent? Icon { get; init; }
}
