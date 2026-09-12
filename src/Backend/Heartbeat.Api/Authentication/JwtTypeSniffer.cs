using System.Text;
using System.Text.Json;

namespace Heartbeat.Api.Authentication;

public static class JwtTypeSniffer
{
    public static bool IsOidcAccessToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var separator = token.IndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        try
        {
            var header = token[..separator].Replace('-', '+').Replace('_', '/');
            header = header.PadRight(header.Length + (4 - header.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(
                Encoding.UTF8.GetString(Convert.FromBase64String(header)));
            return document.RootElement.ValueKind is JsonValueKind.Object
                && document.RootElement.TryGetProperty("typ", out var type)
                && type.ValueKind is JsonValueKind.String
                && string.Equals(
                    type.GetString(),
                    "at+jwt",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or DecoderFallbackException)
        {
            return false;
        }
    }
}
