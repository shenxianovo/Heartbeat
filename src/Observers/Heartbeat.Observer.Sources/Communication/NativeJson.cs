using System.Globalization;
using System.Text.Json;

namespace Heartbeat.Observers.Sources.Communication;

internal static class NativeJson
{
    internal const int MaximumTextCharacters = 256 * 1024;

    internal static async Task<JsonDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await SourceJson.ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            Validate(document.RootElement, cancellationToken);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void Validate(JsonElement element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                {
                    throw new InvalidDataException($"Duplicate native JSON field '{property.Name}'.");
                }

                Validate(property.Value, cancellationToken);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Validate(item, cancellationToken);
            }
        }
        else if (element.ValueKind == JsonValueKind.String && element.GetString()!.Length > MaximumTextCharacters)
        {
            throw new InvalidDataException($"Decoded native string exceeds {MaximumTextCharacters} characters.");
        }
    }

    internal static string RequiredString(JsonElement element, string name)
    {
        return OptionalString(element, name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Required native string '{name}' is missing or empty.");
    }

    internal static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new InvalidDataException($"Native field '{name}' must be a string.");
    }

    internal static string Key(params string[] components) => JsonSerializer.Serialize(components);

    internal static DateTimeOffset? GraphTime(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        if (value is null)
        {
            return null;
        }

        // Native Graph date-time fields require explicit UTC/offset, never the host timezone.
        var separator = value.IndexOf('T', StringComparison.Ordinal);
        var hasOffset = value.EndsWith('Z') || (separator >= 0 &&
            (value.IndexOf('+', separator) >= 0 || value.IndexOf('-', separator) >= 0));
        if (separator < 0 || !hasOffset || !element.GetProperty(name).TryGetDateTimeOffset(out var result))
        {
            throw new InvalidDataException($"Invalid native Graph timestamp '{name}'.");
        }

        return result;
    }

    internal static DateTimeOffset? SlackTime(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var parts = value.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length != 6
            || !parts[0].All(char.IsAsciiDigit) || !parts[1].All(char.IsAsciiDigit)
            || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new InvalidDataException("Invalid native Slack timestamp.");
        }

        try
        {
            return DateTimeOffset.UnixEpoch.AddTicks(checked((long)(seconds * TimeSpan.TicksPerSecond)));
        }
        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("Native Slack timestamp is out of range.", error);
        }
    }

    internal static void Copy(JsonElement source, Dictionary<string, object?> target, string name, JsonValueKind kind)
    {
        if (!source.TryGetProperty(name, out var value))
        {
            return;
        }

        if (value.ValueKind != kind && value.ValueKind != JsonValueKind.Null &&
            !(kind == JsonValueKind.True && value.ValueKind == JsonValueKind.False))
        {
            throw new InvalidDataException($"Native field '{name}' has an invalid type.");
        }

        target.Add(name, value.Clone());
    }
}
