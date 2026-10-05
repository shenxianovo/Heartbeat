using System.Text;

namespace Heartbeat.Dev;

internal sealed class DotenvFile(IReadOnlyDictionary<string, string> values)
{
    public string? GetSaved(string name) => values.GetValueOrDefault(name);

    public string? Get(string name)
    {
        var environment = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(environment)
            ? values.GetValueOrDefault(name)
            : environment;
    }

    public static DotenvFile Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0 || key.StartsWith('#'))
            {
                continue;
            }
            values[key] = Decode(line[(separator + 1)..].Trim());
        }
        return new DotenvFile(values);
    }

    private static string Decode(string value)
    {
        if (IsQuoted(value, '\''))
            return value[1..^1].Replace("\\'", "'", StringComparison.Ordinal);
        if (IsQuoted(value, '"')) return DecodeDoubleQuoted(value);
        return StripInlineComment(value);
    }

    private static bool IsQuoted(string value, char quote) =>
        value.Length >= 2 && value[0] == quote && value[^1] == quote;

    private static string DecodeDoubleQuoted(string value)
    {
        var decoded = new StringBuilder();
        for (var index = 1; index < value.Length - 1; index++)
        {
            var character = value[index];
            if (character != '\\' || index + 1 >= value.Length - 1)
            {
                decoded.Append(character);
                continue;
            }
            decoded.Append(DecodeEscape(value[++index]));
        }
        return decoded.ToString().Replace("$$", "$", StringComparison.Ordinal);
    }

    private static string DecodeEscape(char value) => value switch
    {
        'n' => "\n",
        'r' => "\r",
        't' => "\t",
        '"' => "\"",
        '\\' => "\\",
        _ => $"\\{value}",
    };

    private static string StripInlineComment(string value)
    {
        var comment = value.IndexOfAny([' ', '\t']);
        if (comment >= 0)
        {
            var tail = value[comment..].TrimStart();
            if (tail.StartsWith('#'))
            {
                value = value[..comment];
            }
        }
        return value.TrimEnd();
    }
}
