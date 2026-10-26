namespace Orisumi.Core;

// Position is a JSON Pointer rooted at the version, including array indices.
public sealed record VersionReference(string Position, VersionId Target);
