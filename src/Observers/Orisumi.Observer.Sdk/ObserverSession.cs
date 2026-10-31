using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Orisumi.Observer.Sdk;

public interface IObserverSession
{
    Task<JsonElement> RequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default);
}

/// <summary>The backend rejected the submission formed from a batch; nothing in the batch was saved.</summary>
public sealed class SubmissionRejectedException(string rule, Guid versionId, string message) : Exception(message)
{
    public string Rule { get; } = rule;
    public Guid VersionId { get; } = versionId;
}

/// <summary>A language adapter: only framing, request correlation and host lifetime.</summary>
public sealed class PipeObserverSession : IObserverSession, IAsyncDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _requests = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource<JsonElement> _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopped = new();
    private readonly Task _reading;
    private bool _disposed;
    public CancellationToken Stopping => _stopped.Token;
    public Task<JsonElement> Initialization => _initialization.Task;

    public PipeObserverSession(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
        _reading = Task.Run(ReadAsync);
    }

    public async Task<JsonElement> RequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requests.WaitAsync(cancellationToken);
        var id = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (_reading.IsCompleted) throw new IOException("The Runtime connection is closed.");
            _pending[id] = completion;
            var line = JsonSerializer.Serialize(new { id, method, @params = parameters });
            await JsonLines.WriteAsync(_output, line, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally { _pending.TryRemove(id, out _); _requests.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception failure = new IOException("The Runtime connection is closed.");
        try
        {
            while (await JsonLines.ReadAsync(_input, CancellationToken.None) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var value = json.RootElement;
                if (value.TryGetProperty("event", out var kind))
                {
                    if (kind.GetString() == "init") _initialization.TrySetResult(value.Clone());
                    if (kind.GetString() == "stop") await _stopped.CancelAsync();
                    continue;
                }
                if (!value.TryGetProperty("id", out var id) || !_pending.TryGetValue(id.GetString()!, out var completion)) continue;
                if (value.TryGetProperty("error", out var error)) completion.TrySetException(Failure(error));
                else completion.TrySetResult(value.GetProperty("result").Clone());
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException) { failure = exception; }
        finally
        {
            _initialization.TrySetException(failure);
            foreach (var pending in _pending.Values) pending.TrySetException(failure);
            await _stopped.CancelAsync();
        }
    }

    private static Exception Failure(JsonElement error)
    {
        var message = error.GetProperty("message").GetString();
        return error.GetProperty("code").GetString() switch
        {
            "unavailable" => new HttpRequestException(message),
            "rejected" => new SubmissionRejectedException(error.GetProperty("rule").GetString()!, error.GetProperty("versionId").GetGuid(), message!),
            _ => new InvalidOperationException(message),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stopped.CancelAsync();
        foreach (var pending in _pending.Values) pending.TrySetException(new ObjectDisposedException(nameof(PipeObserverSession)));
        // Standard streams belong to the process. The host waits for this process to exit,
        // so disposal must not wait for the host to close stdin.
        if (_reading.IsCompleted)
        {
            _stopped.Dispose();
            _requests.Dispose();
        }
    }

}

public static class JsonLines
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
