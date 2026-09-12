using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Heartbeat.Hub.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Heartbeat.Hub.Tests;

public sealed class HubCrashTests
{
    [Fact]
    public async Task RestartedHubUploadsExistingCustodyWithoutAnyCollectorConnection()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var backend = builder.Build();
        var delivered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collectorId = Guid.CreateVersion7();
        var trackId = Guid.CreateVersion7();
        backend.MapPost("/api/v1/collectors", () => Results.Ok(new
        {
            id = collectorId,
            key = QueueFixture.Collector().Key,
            target = QueueFixture.Collector().Target,
        }));
        backend.MapPost("/api/v1/collectors/{id:guid}/tracks", (TrackDeclaration declaration) => Results.Ok(new
        {
            id = trackId,
            collectorId,
            declaration.Type,
            declaration.Version,
            declaration.TimeMode,
            declaration.EndMode,
        }));
        backend.MapPost("/api/v1/tracks/{id:guid}/records", (UploadBatch batch) =>
        {
            var record = Assert.Single(batch.Records);
            delivered.TrySetResult(record.Id);
            return Results.Ok(new
            {
                results = new[] { new { index = 0, record.Id, status = "stored", record.EndedAt, receivedAt = DateTimeOffset.UtcNow } },
            });
        });
        await backend.StartAsync();
        using var fixture = new QueueFixture
        {
            Destination = new DeliveryDestination(new Uri(Assert.Single(backend.Urls)), Guid.NewGuid()),
        };
        var record = QueueFixture.Snapshot();
        fixture.Open().Accept(QueueFixture.Submission(record));
        const string accessToken = "restart-test-secret-with-at-least-32-characters";
        var hub = await StartAsync(fixture, accessToken);
        try
        {
            Assert.Equal(record.Id, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            using var client = Client(hub.Url, accessToken);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await client.GetFromJsonAsync<QueueStatus>("hub/v1/status", deadline.Token))!.Pending != 0)
            {
                await Task.Delay(20, deadline.Token);
            }

            Assert.Equal(new QueueStatus(0, 0), fixture.Open().Status());
        }
        finally
        {
            await KillAsync(hub.Process);
            await backend.StopAsync();
        }
    }

    [Fact]
    public async Task AcknowledgedRecordSurvivesForcedProcessTerminationAndHostRestart()
    {
        using var fixture = new QueueFixture();
        const string accessToken = "crash-test-local-secret-at-least-32-characters";
        var record = QueueFixture.Snapshot();
        var first = await StartAsync(fixture, accessToken);
        try
        {
            using var client = Client(first.Url, accessToken);
            using var response = await client.PostAsJsonAsync("hub/v1/records", QueueFixture.Submission(record));
            response.EnsureSuccessStatusCode();
            // Kill after the complete custody response; never request a graceful shutdown or drain.
        }
        finally
        {
            await KillAsync(first.Process);
        }

        var second = await StartAsync(fixture, accessToken);
        try
        {
            using var client = Client(second.Url, accessToken);
            Assert.Equal(new QueueStatus(1, 0), await client.GetFromJsonAsync<QueueStatus>("hub/v1/status"));
            using var retry = await client.PostAsJsonAsync("hub/v1/records", QueueFixture.Submission(record));
            retry.EnsureSuccessStatusCode();
            Assert.Equal(new QueueStatus(1, 0), await client.GetFromJsonAsync<QueueStatus>("hub/v1/status"));
        }
        finally
        {
            await KillAsync(second.Process);
        }

        Assert.Equal(record.Id, Assert.Single(fixture.Open().TakePending()).Record.Id);
    }

    private static async Task<(Process Process, Uri Url)> StartAsync(QueueFixture fixture, string accessToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(HubProgram).Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["Hub__DatabasePath"] = fixture.DatabasePath;
        start.Environment["Hub__BackendUrl"] = fixture.Destination.BackendUrl.AbsoluteUri;
        start.Environment["Hub__OwnerId"] = fixture.Destination.OwnerId.ToString();
        start.Environment["Hub__BackendToken"] = fixture.Token;
        start.Environment["Hub__AccessToken"] = accessToken;
        start.Environment["Hub__UploadIntervalSeconds"] = "1";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, args) =>
        {
            const string prefix = "Now listening on: ";
            var index = args.Data?.IndexOf(prefix, StringComparison.Ordinal) ?? -1;
            if (index >= 0 && Uri.TryCreate(args.Data![(index + prefix.Length)..].Trim(), UriKind.Absolute, out var url))
            {
                ready.TrySetResult(url);
            }
        };
        process.Exited += (_, _) => ready.TrySetException(new InvalidOperationException("Hub exited before listening."));
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            return (process, await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        }
        catch
        {
            await KillAsync(process);
            throw;
        }
    }

    private static HttpClient Client(Uri url, string accessToken)
    {
        var client = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task KillAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        process.Dispose();
    }

    private sealed record UploadBatch(IReadOnlyList<RecordSnapshot> Records);
}
