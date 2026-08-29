using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
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
public sealed class RecapHttpTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private readonly FakeGenerator _generator = new();

    protected override async Task SeedAsync(AppDbContext db)
    {
        db.Users.Add(new User { Id = "user-1", Username = "alice", IsPublic = true });
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "Test PC" };
        var app = new App { Name = "VSCode" };
        db.AddRange(device, app);
        await db.SaveChangesAsync();
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = device.Id,
            Source = ActivitySources.System,
            IdentityKey = SystemIdentity.Key("VSCode", null),
            AppId = app.Id,
            StartTime = DateTimeOffset.Parse("2026-03-08T05:00:00Z"),
            EndTime = DateTimeOffset.Parse("2026-03-08T06:00:00Z"),
        });
        var window = ResolveWindow();
        db.Recaps.Add(new Recap
        {
            OwnerId = "user-1",
            WindowKey = window.WindowKey.Value,
            WindowVersion = window.Version,
            WindowKind = window.Kind,
            LocalDate = window.LocalDate,
            TimeZone = window.TimeZone,
            WindowStart = window.Start,
            WindowEndExclusive = window.EndExclusive,
            Narrative = "cached narrative",
            GeneratedAt = window.Start,
            Model = "seed",
            PromptHash = "deadbeef",
            SegmentWatermark = window.Start.AddHours(1),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task OwnerPublicAndSseRoutesConsumeTheSameCompleteDayEnvelope()
    {
        await using var app = CreateApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var owner = await client.GetAsync("/api/v1/recaps/daily?" + WindowQuery());
        var publicUser = await client.GetAsync("/api/v1/users/alice/recaps/daily?" + WindowQuery());

        owner.EnsureSuccessStatusCode();
        publicUser.EnsureSuccessStatusCode();
        var ownerJson = JsonDocument.Parse(await owner.Content.ReadAsStringAsync());
        var publicJson = JsonDocument.Parse(await publicUser.Content.ReadAsStringAsync());
        Assert.Equal("cached narrative", ownerJson.RootElement.GetProperty("narrative").GetString());
        Assert.Equal(
            ownerJson.RootElement.GetProperty("narrative").GetString(),
            publicJson.RootElement.GetProperty("narrative").GetString());
        Assert.Equal(0, _generator.Calls);

        using var generated = await client.PostAsync("/api/v1/recaps/daily/generate?" + WindowQuery(), null);
        generated.EnsureSuccessStatusCode();
        var stream = await generated.Content.ReadAsStringAsync();
        Assert.Contains("event: delta", stream);
        Assert.Contains("generated narrative", stream);
        Assert.Equal(1, _generator.Calls);
    }

    [Fact]
    public async Task RecapRoutesRequireTheEnvelopeAndReturnStableMismatchErrorsWithoutGeneration()
    {
        await using var app = CreateApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var missing = await client.GetAsync("/api/v1/recaps/daily?date=2026-03-08");
        var mismatch = await client.PostAsync(
            "/api/v1/recaps/daily/generate?" + WindowQuery("2026-03-09T04:00:01Z"), null);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var mismatchJson = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
        Assert.Equal("calendar_rules_mismatch", mismatchJson.RootElement.GetProperty("code").GetString());
        Assert.Contains("TZDB", mismatchJson.RootElement.GetProperty("message").GetString());
        Assert.Equal(0, _generator.Calls);
    }

    [Fact]
    public async Task ConcurrentGenerationRequestsLockTheSameCanonicalWindowButNotADistinctValidWindow()
    {
        _generator.BlockAfterFirstChunk();
        await using var app = CreateApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var first = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/recaps/daily/generate?" + WindowQuery()),
            HttpCompletionOption.ResponseHeadersRead);
        first.EnsureSuccessStatusCode();
        await _generator.WaitForCallsAsync(1);

        using var duplicate = await client.PostAsync(
            "/api/v1/recaps/daily/generate?" + WindowQuery(), null);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var distinctTask = client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/recaps/daily/generate?" + UtcWindowQuery()),
            HttpCompletionOption.ResponseHeadersRead);
        await _generator.WaitForCallsAsync(2);
        using var distinct = await distinctTask;
        distinct.EnsureSuccessStatusCode();

        _generator.Release();
        await first.Content.ReadAsStringAsync();
        await distinct.Content.ReadAsStringAsync();
        first.Dispose();
        Assert.Equal(2, _generator.Calls);
    }

    [Fact]
    public async Task OpenApiMarksEveryRecapReadEnvelopeFieldAsRequired()
    {
        await using var app = CreateApplication();
        using var client = app.CreateClient();
        var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        foreach (var path in new[] { "/api/v1/recaps/daily", "/api/v1/users/{username}/recaps/daily" })
        {
            var parameters = json.RootElement
                .GetProperty("paths")
                .GetProperty(path)
                .GetProperty("get")
                .GetProperty("parameters")
                .EnumerateArray()
                .Where(parameter => parameter.GetProperty("name").GetString() != "username")
                .ToArray();
            Assert.Equal(6, parameters.Length);
            Assert.All(parameters, parameter => Assert.True(parameter.GetProperty("required").GetBoolean()));
        }
    }

    private WebApplicationFactory<RecapController> CreateApplication() =>
        new WebApplicationFactory<RecapController>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IRecapGenerator>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(TestConnectionString));
                services.AddSingleton<IRecapGenerator>(_generator);
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                        options.DefaultScheme = "Test";
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        });

    private static ResolvedCalendarWindow ResolveWindow()
    {
        var result = LocalCalendarWindowValidator.ResolveDay(new LocalCalendarWindowEnvelope
        {
            Version = 1,
            Kind = "day",
            LocalDate = "2026-03-08",
            TimeZone = "America/New_York",
            Start = DateTimeOffset.Parse("2026-03-08T05:00:00Z"),
            EndExclusive = DateTimeOffset.Parse("2026-03-09T04:00:00Z"),
        });
        return result.Window!;
    }

    private static string WindowQuery(string endExclusive = "2026-03-09T04:00:00Z") =>
        "version=1&kind=day&localDate=2026-03-08&timeZone=America%2FNew_York" +
        "&start=2026-03-08T05%3A00%3A00Z&endExclusive=" + Uri.EscapeDataString(endExclusive);

    private static string UtcWindowQuery() =>
        "version=1&kind=day&localDate=2026-03-08&timeZone=Etc%2FUTC" +
        "&start=2026-03-08T00%3A00%3A00Z&endExclusive=2026-03-09T00%3A00%3A00Z";

    private sealed class FakeGenerator : IRecapGenerator
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _callsChanged = new(0);
        private bool _block;
        public int Calls;
        public string Model => "fake";
        public string PromptHash => "deadbeef";

        public void BlockAfterFirstChunk() => _block = true;
        public void Release() => _release.TrySetResult();

        public async Task WaitForCallsAsync(int expected)
        {
            while (Volatile.Read(ref Calls) < expected)
                Assert.True(
                    await _callsChanged.WaitAsync(TimeSpan.FromSeconds(5)),
                    $"Generator did not reach {expected} calls before the timeout.");
        }

        public async IAsyncEnumerable<LlmChunk> GenerateStreamAsync(
            string digest,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            _callsChanged.Release();
            yield return LlmChunk.OfReasoning("started");
            if (_block) await _release.Task.WaitAsync(ct);
            yield return LlmChunk.OfContent("generated narrative");
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims = [new("sub", "user-1"), new("preferred_username", "alice")];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
