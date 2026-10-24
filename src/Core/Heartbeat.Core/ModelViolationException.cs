namespace Heartbeat.Core;

public sealed class ModelViolationException(string rule, string message) : Exception(message)
{
    public string Rule { get; } = rule;
}
