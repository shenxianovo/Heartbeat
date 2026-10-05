namespace Heartbeat.Recording;

internal static class TextValue
{
    public static string NormalizeRequired(
        string? value,
        string parameterName,
        int? maximumLength = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();

        if (maximumLength is { } limit && normalized.Length > limit)
        {
            throw new ArgumentException(
                $"The value cannot exceed {limit} characters.",
                parameterName);
        }

        return normalized;
    }
}
