using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Orisumi.Observer.Runtime.Forwarding;

internal sealed class ForwardingServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, WriteQueue> _queues = [];
    private WebApplication? _app;
    internal string Address { get; private set; } = null!;
    internal void Remove(Guid instance) => _queues.TryRemove(instance, out _);
    internal void Add(Guid instance, WriteQueue queue) => _queues[instance] = queue;
    internal string InstanceAddress(Guid id) => Address + $"/instances/{id}/";
    internal async Task StartAsync(CancellationToken token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = WriteQueue.PostLimit);
        _app = builder.Build();
        _app.MapPost("/instances/{instance:guid}/writes", (Guid instance, HttpContext context) => AcceptAsync(instance, null, context));
        _app.MapPut("/instances/{instance:guid}/contents/{digest}", (Guid instance, string digest, HttpContext context) => AcceptAsync(instance, digest, context));
        await _app.StartAsync(token);
        Address = _app.Urls.Single();
    }
    private async Task<IResult> AcceptAsync(Guid instance, string? digest, HttpContext context)
    {
        if (!_queues.TryGetValue(instance, out var queue)) return Results.NotFound();
        var limit = digest is null ? WriteQueue.PostLimit : WriteQueue.PutLimit;
        if (context.Request.ContentLength > limit) return Results.StatusCode(413);
        // The digest forms a file name. Validate its syntax without interpreting the bytes.
        if (digest is not null && !Orisumi.Core.BinaryValue.ValidDigest(digest)) return Results.BadRequest();
        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[81920]; int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (body.Length + read > limit) return Results.StatusCode(413);
                body.Write(buffer, 0, read);
            }
            await queue.EnqueueAsync(body.ToArray(), digest, context.RequestAborted);
            return Results.StatusCode(digest is null ? 202 : 204);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Results.StatusCode(413); }
        catch (IOException) { return Results.StatusCode(507); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
    }
}
