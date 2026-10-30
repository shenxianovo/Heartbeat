namespace Orisumi.Core;

public sealed class ModelViolationException(string rule, string message, bool conflict = false) : Exception(message)
{
    public string Rule { get; } = rule;
    public bool Conflict { get; } = conflict;
}
