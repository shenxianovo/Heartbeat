using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Heartbeat.Hub.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Hub.Tests;

public sealed class HubHttpTests : IDisposable
{
    private const string AccessToken = "local-test-secret-with-at-least-32-characters";
    private readonly QueueFixture _fixture = new();

    [Fact]
    public async Task DesktopCanTransferCustodyWithoutContactingTheBackend()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var record = QueueFixture.Snapshot();
        await new HubSubmissionClient(client).SubmitAsync(QueueFixture.Submission(record), cancellationToken: TestContext.Current.CancellationToken);

        var status = await client.GetFromJsonAsync<QueueStatus>("/hub/v1/status", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new QueueStatus(1, 0), status);
        var restored = Assert.Single(_fixture.Open().TakePending());
        Assert.Equal(record.Id, restored.Record.Id);
        Assert.Equal(QueueFixture.Collector(), restored.Route.Collector);
        Assert.Equal(QueueFixture.Track(), restored.Route.Track);
        Assert.Null(restored.Route.BackendTrackId);
    }

    [Fact]
    public async Task MissingOrWrongHubTokenCannotWriteOrInspectTheQueue()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await Submit(client, QueueFixture.Snapshot());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-secret");
        using var status = await client.GetAsync("/hub/v1/status", cancellationToken: TestContext.Current.CancellationToken);
        using var failures = await client.GetAsync("/hub/v1/failures", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, failures.StatusCode);
        Assert.Equal(new QueueStatus(0, 0), _fixture.Open().Status());
    }

    [Fact]
    public async Task SqliteFailureReturnsNoCustodyAcknowledgement()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        using var connection = new SqliteConnection($"Data Source={_fixture.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_write BEFORE INSERT ON records
            BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END;
            """;
        command.ExecuteNonQuery();
        using var response = await Submit(client, QueueFixture.Snapshot());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new QueueStatus(0, 0), _fixture.Open().Status());
    }

    [Fact]
    public async Task UnknownFieldsAndInvalidBatchesAreRejectedBeforeStorage()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        using var unknown = await client.PostAsJsonAsync("/hub/v1/records",
            new { collector = QueueFixture.Collector(), track = QueueFixture.Track(), records = new[] { QueueFixture.Snapshot() }, ownerId = Guid.NewGuid() }, cancellationToken: TestContext.Current.CancellationToken);
        using var invalid = await Submit(client, QueueFixture.Snapshot() with { EndedAt = null });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(new QueueStatus(0, 0), _fixture.Open().Status());
    }

    [Fact]
    public async Task ClientReportsHubValidationDetailForRejectedSubmission()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var invalid = QueueFixture.Submission(QueueFixture.Snapshot() with { EndedAt = null });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new HubSubmissionClient(client).SubmitAsync(invalid, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Record end time does not match its Track declaration", error.Message);
    }

    [Fact]
    public async Task RecordConflictDoesNotAcknowledgeOtherItemsInTheSameLocalBatch()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var record = QueueFixture.Snapshot();
        using var first = await Submit(client, record);
        first.EnsureSuccessStatusCode();
        using var conflict = await Submit(client, QueueFixture.Snapshot(), record with { StartedAt = record.StartedAt!.Value.AddSeconds(-1) });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(new QueueStatus(1, 0), _fixture.Open().Status());
    }

    private static Task<HttpResponseMessage> Submit(HttpClient client, params RecordSnapshot[] records) =>
        client.PostAsJsonAsync("/hub/v1/records", QueueFixture.Submission(records));

    private WebApplicationFactory<HubProgram> Factory() =>
        new WebApplicationFactory<HubProgram>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:DataDirectory"] = _fixture.DirectoryPath,
                ["Hub:BackendUrl"] = _fixture.Destination.BackendUrl.AbsoluteUri,
                ["Hub:OwnerId"] = _fixture.Destination.OwnerId.ToString(),
                ["Hub:AuthUrl"] = "https://auth.example/",
                ["Hub:ApiKey"] = "test-api-key",
                ["Hub:AccessToken"] = AccessToken,
            }));
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

    private static HttpClient Client(WebApplicationFactory<HubProgram> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return client;
    }

    public void Dispose() => _fixture.Dispose();
}
