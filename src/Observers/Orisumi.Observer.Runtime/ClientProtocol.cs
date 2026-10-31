using System.Text;
using System.Text.Json;
namespace Orisumi.Observer.Runtime;

internal static class ClientProtocol
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public static async Task<string?> ReadAsync(TextReader reader, CancellationToken token)
    {
        // Bound before allocating a complete untrusted line, including readers other than StreamReader.
        var builder = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') break;
            if (builder.Length >= MaximumBytes) throw new InvalidDataException("Protocol line exceeds 16 MiB.");
            builder.Append(buffer[0]);
        }
        if (builder.Length == 0) return null;
        var line = builder.ToString().TrimEnd('\r');
        if (Encoding.UTF8.GetByteCount(line) > MaximumBytes) throw new InvalidDataException("Protocol line exceeds 16 MiB.");
        return line;
    }
    public static async Task WriteAsync(TextWriter writer, string line, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaximumBytes) throw new InvalidDataException("Protocol line exceeds 16 MiB.");
        await writer.WriteLineAsync(line.AsMemory(), token);
        await writer.FlushAsync(token);
    }
    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}
