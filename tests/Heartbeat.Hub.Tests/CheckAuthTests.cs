using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Hub.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Heartbeat.Hub.Tests;

public sealed class CheckAuthTests
{
    [Fact]
    public async Task CheckAuthPrintsOnlyTheOwnerJsonWithoutStartingHubServices()
    {
        var ownerId = Guid.NewGuid();
        const string apiKey = "check-auth-api-key";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var auth = builder.Build();
        auth.MapPost("/api/v1/apikeys/exchange", async (HttpRequest request) =>
        {
            var body = await request.ReadFromJsonAsync<ExchangeRequest>();
            Assert.Equal(apiKey, body!.ApiKey);
            return Results.Ok(new
            {
                accessToken = QueueFixture.TokenFor(ownerId),
                expiresIn = 3600,
            });
        });
        await auth.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await RunAsync(new Uri(Assert.Single(auth.Urls)), apiKey);

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput.Trim());
        Assert.Equal(ownerId, output.RootElement.GetProperty("ownerId").GetGuid());
        Assert.Single(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(string.Empty, result.StandardError);
        await auth.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CheckAuthFailureIsNonzeroAndDoesNotRevealCredentials()
    {
        const string apiKey = "secret-api-key-that-must-not-appear";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var auth = builder.Build();
        auth.MapPost("/api/v1/apikeys/exchange", () => Results.Unauthorized());
        await auth.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await RunAsync(new Uri(Assert.Single(auth.Urls)), apiKey);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.DoesNotContain(apiKey, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", result.StandardError, StringComparison.OrdinalIgnoreCase);
        await auth.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<ProcessResult> RunAsync(Uri authUrl, string apiKey)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(HubProgram).Assembly.Location);
        start.ArgumentList.Add("--check-auth");
        foreach (var name in new[]
                 {
                     "Hub__DataDirectory", "Hub__BackendUrl", "Hub__OwnerId", "Hub__AccessToken",
                     "Hub__MaximumRecords", "Hub__UploadIntervalSeconds",
                 })
        {
            start.Environment.Remove(name);
        }

        start.Environment["Hub__AuthUrl"] = authUrl.AbsoluteUri;
        start.Environment["Hub__ApiKey"] = apiKey;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ExchangeRequest(string ApiKey);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
