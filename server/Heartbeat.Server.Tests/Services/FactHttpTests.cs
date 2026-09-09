using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class FactHttpTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task NativeHttpBoundary_RequiresOwnerAndPreservesRawDocumentAndUrl_ConflictsAreAtomic()
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        var batch = FactStoreTests.SegmentBatch();
        using (var anonymous = await client.PostAsJsonAsync("/api/v1/facts", batch))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using (var upload = await client.PostAsJsonAsync("/api/v1/facts", batch))
        {
            Assert.True(upload.StatusCode == HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());
        }
        using (var read = await client.GetAsync("/api/v1/users/alice/segments"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("https://example.com/?original=1", row.GetProperty("payload").GetProperty("attributes").GetProperty("url").GetString());
            Assert.Equal(batch.Streams[0].StreamId, row.GetProperty("streamId").GetGuid());
            Assert.Equal(batch.Facts[0].FactId, row.GetProperty("factId").GetGuid());
            Assert.Equal("native", row.GetProperty("origin").GetString());
        }
        batch.Facts.Insert(0, new FactSnapshot
        {
            StreamId = batch.Streams[0].StreamId, FactId = Guid.CreateVersion7(), Revision = 1, SchemaRevision = 1,
            Start = batch.Facts[0].Start, End = batch.Facts[0].End, IsFinal = false, Payload = batch.Facts[0].Payload
        });
        batch.Facts[1].Payload = JsonSerializer.SerializeToElement(new { identityKey = "different", title = "Changed" });
        using (var conflict = await client.PostAsJsonAsync("/api/v1/facts", batch))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using (var db = CreateDbContext())
        {
            Assert.Single(await db.Facts.ToListAsync());
            Assert.Single(await db.ActivitySegments.ToListAsync());
            Assert.Equal(batch.Streams[0].Schemas[0].DocumentJson, (await db.FactSchemas.SingleAsync()).DocumentJson);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeHttpBoundary_RejectsOldRetractionWithoutChangingStoredFact(bool includePayload)
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        client.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var batch = FactStoreTests.SegmentBatch();
        using var accepted = await client.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var request = JsonSerializer.SerializeToNode(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var fact = request["facts"]![0]!.AsObject();
        fact["recordState"] = "retracted";
        fact["revision"] = 2;
        if (!includePayload) fact.Remove("payload");

        using var rejected = await client.PostAsJsonAsync("/api/v1/facts", request);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("recordState", await rejected.Content.ReadAsStringAsync());
        await using var verify = CreateDbContext();
        Assert.Equal(1, (await verify.Facts.SingleAsync()).Revision);
        Assert.Equal(batch.Facts[0].End, (await verify.ActivitySegments.SingleAsync()).EndTime);
    }

    private WebApplicationFactory<FactController> CreateApplication() =>
        new WebApplicationFactory<FactController>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(TestConnectionString));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                    options.DefaultScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        });

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var owner = Request.Headers["X-Test-Owner"].FirstOrDefault();
            if (owner is null) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", owner), new Claim("preferred_username", "alice")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
