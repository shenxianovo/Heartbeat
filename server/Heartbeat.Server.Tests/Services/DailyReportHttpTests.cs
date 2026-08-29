using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heartbeat.Core;
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
public sealed class DailyReportHttpTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
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
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task OwnerAndPublicRoutes_BindAndSerializeTheSameCompleteEnvelope()
    {
        await using var app = CreateApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var owner = await client.GetAsync("/api/v1/reports/daily?" + WindowQuery());
        var publicUser = await client.GetAsync("/api/v1/users/alice/reports/daily?" + WindowQuery());

        owner.EnsureSuccessStatusCode();
        publicUser.EnsureSuccessStatusCode();
        var ownerJson = JsonDocument.Parse(await owner.Content.ReadAsStringAsync());
        var publicJson = JsonDocument.Parse(await publicUser.Content.ReadAsStringAsync());
        Assert.Equal("2026-03-08", ownerJson.RootElement.GetProperty("date").GetString());
        Assert.Equal(
            ownerJson.RootElement.GetProperty("apps")[0].GetProperty("durationSeconds").GetInt32(),
            publicJson.RootElement.GetProperty("apps")[0].GetProperty("durationSeconds").GetInt32());
    }

    [Fact]
    public async Task HttpContract_RequiresTheEnvelopeAndPreservesDiagnosticMismatch()
    {
        await using var app = CreateApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var missing = await client.GetAsync("/api/v1/reports/daily?date=2026-03-08");
        var mismatch = await client.GetAsync(
            "/api/v1/reports/daily?" + WindowQuery(endExclusive: "2026-03-09T04:00:01Z"));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var mismatchJson = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
        Assert.Equal("calendar_rules_mismatch", mismatchJson.RootElement.GetProperty("code").GetString());
        Assert.Contains("TZDB", mismatchJson.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OwnerAndPublicWeeklyRoutes_BindAndSerializeTheSameCompleteEnvelope()
    {
        await using var app = CreateApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var owner = await client.GetAsync("/api/v1/reports/weekly?" + WeekWindowQuery());
        var publicUser = await client.GetAsync(
            "/api/v1/users/alice/reports/weekly?" + WeekWindowQuery());

        owner.EnsureSuccessStatusCode();
        publicUser.EnsureSuccessStatusCode();
        var ownerJson = JsonDocument.Parse(await owner.Content.ReadAsStringAsync());
        var publicJson = JsonDocument.Parse(await publicUser.Content.ReadAsStringAsync());
        Assert.Equal("2026-03-02", ownerJson.RootElement.GetProperty("weekStart").GetString());
        Assert.Equal("2026-03-08", ownerJson.RootElement.GetProperty("weekEnd").GetString());
        Assert.Equal(
            ownerJson.RootElement.GetProperty("apps")[0].GetProperty("durationSeconds").GetInt32(),
            publicJson.RootElement.GetProperty("apps")[0].GetProperty("durationSeconds").GetInt32());
    }

    [Fact]
    public async Task WeeklyHttpContract_RequiresTheEnvelopeAndPreservesDiagnosticMismatch()
    {
        await using var app = CreateApplication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var missing = await client.GetAsync("/api/v1/reports/weekly?date=2026-03-08");
        var mismatch = await client.GetAsync(
            "/api/v1/reports/weekly?" + WeekWindowQuery(endExclusive: "2026-03-09T04:00:01Z"));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var mismatchJson = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
        Assert.Equal("calendar_rules_mismatch", mismatchJson.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OpenApi_MarksEveryCalendarEnvelopeFieldAsRequired()
    {
        await using var app = CreateApplication();
        using var client = app.CreateClient();

        var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        string[] paths =
        [
            "/api/v1/reports/daily",
            "/api/v1/reports/weekly",
            "/api/v1/users/{username}/reports/daily",
            "/api/v1/users/{username}/reports/weekly",
        ];

        foreach (var path in paths)
        {
            var parameters = json.RootElement
                .GetProperty("paths")
                .GetProperty(path)
                .GetProperty("get")
                .GetProperty("parameters")
                .EnumerateArray()
                .Where(parameter => parameter.GetProperty("name").GetString() is not ("deviceId" or "username"))
                .ToArray();

            Assert.Equal(6, parameters.Length);
            Assert.All(parameters, parameter => Assert.True(parameter.GetProperty("required").GetBoolean(),
                $"{path} {parameter.GetProperty("name").GetString()} should be required"));
        }
    }

    private WebApplicationFactory<ReportController> CreateApplication() =>
        new WebApplicationFactory<ReportController>().WithWebHostBuilder(builder =>
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
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        });

    private static string WindowQuery(string endExclusive = "2026-03-09T04:00:00Z") =>
        "version=1&kind=day&localDate=2026-03-08&timeZone=America%2FNew_York" +
        "&start=2026-03-08T05%3A00%3A00Z&endExclusive=" + Uri.EscapeDataString(endExclusive);

    private static string WeekWindowQuery(string endExclusive = "2026-03-09T04:00:00Z") =>
        "version=1&kind=week&localDate=2026-03-08&timeZone=America%2FNew_York" +
        "&start=2026-03-02T05%3A00%3A00Z&endExclusive=" + Uri.EscapeDataString(endExclusive);

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims =
            [
                new("sub", "user-1"),
                new("preferred_username", "alice"),
            ];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
