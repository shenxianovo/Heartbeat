using Orisumi.Core;

namespace Orisumi.Observers.ForegroundState;

internal sealed class ForegroundApplicationContent
{
    public required EntityId Id { get; init; }
    public required string? Name { get; init; }
    public Orisumi.Observer.Sdk.BinaryContent? Icon { get; init; }
}
