namespace Heartbeat.Recording;

public sealed class CollectorRegistration
{
    private CollectorRegistration(string key, string target, string displayName)
    {
        Key = key;
        Target = target;
        DisplayName = displayName;
    }

    public string Key { get; }

    public string Target { get; }

    public string DisplayName { get; }

    public static CollectorRegistration Create(
        string? key,
        string? target,
        string? displayName)
    {
        var normalizedKey = NormalizeKey(key);

        return new CollectorRegistration(
            normalizedKey,
            TextValue.NormalizeRequired(target, nameof(target), Collector.MaximumTextLength),
            TextValue.NormalizeRequired(displayName, nameof(displayName), Collector.MaximumTextLength));
    }

    private static string NormalizeKey(string? key)
    {
        var normalized = TextValue.NormalizeRequired(key, nameof(key), Collector.MaximumTextLength);
        var segments = normalized.Split('.');

        if (segments.Length < 2
            || segments.Any(segment => segment.Length == 0)
            || normalized.Any(character =>
                character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '.'
                and not '-'
                and not '_'))
        {
            throw new ArgumentException(
                "A collector key must be a lowercase, dot-separated identifier.",
                nameof(key));
        }

        return normalized;
    }
}
