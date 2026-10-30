using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Api;

internal static class ContentEndpoints
{
    internal const int MaximumBytes = 8 * 1024 * 1024;

    internal static void MapContents(this WebApplication app)
    {
        app.MapPut("/contents/{digest}", UploadAsync).WithName("putContent");
        app.MapGet("/contents/{digest}", async (string digest, HttpResponse response, ContentService service, CancellationToken token) =>
        {
            if (!BinaryValue.ValidDigest(digest)) return ApiProblem.Create(400, "Invalid content digest.");
            var data = await service.ReadAsync(digest, token);
            if (data is null) return ApiProblem.Create(404, "No saved version uses this content.");
            response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.Bytes(data, "application/octet-stream");
        }).WithName("getContent");
    }

    private static async Task<IResult> UploadAsync(string digest, HttpRequest request, ContentService service, CancellationToken token)
    {
        if (!BinaryValue.ValidDigest(digest)) return ApiProblem.Create(400, "Invalid content digest.");
        if (request.ContentLength > MaximumBytes) return ApiProblem.Create(413, "Content must not exceed 8 MiB.");
        if (!string.Equals(request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return ApiProblem.Create(400, "Content-Type must be application/octet-stream.");
        using var data = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaximumBytes + 1 - data.Length)), token)) > 0)
        {
            await data.WriteAsync(buffer.AsMemory(0, read), token);
            if (data.Length > MaximumBytes) return ApiProblem.Create(413, "Content must not exceed 8 MiB.");
        }
        await service.UploadAsync(digest, data.ToArray(), token);
        return Results.NoContent();
    }
}

internal sealed partial class ContentCleanup(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<ContentCleanup> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to remove staged binary content.")]
    private static partial void CleanupFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ContentService>().PruneAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                CleanupFailed(logger, exception);
            }
        }
    }
}
