namespace Heartbeat.Core.DTOs.Facts;

/// <summary>A previously established self reference; authentication does not supply this Target.</summary>
public sealed record PersonReference(Guid Id)
{
    public FactTarget ToTarget() => new("person", Id.ToString("D"));

    public static PersonReference Parse(string reference)
    {
        if (reference is not { Length: 36 } || !Guid.TryParseExact(reference, "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Person Target requires an explicit person UUID.");
        return new PersonReference(id);
    }
}
