using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heartbeat.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class CollectorHttpTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private const string OwnerHeader = "X-Test-Owner";
    private static readonly DateTimeOffset Now =
        new(2026, 9, 12, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthenticatedOwnerCanRegisterCollector()
    {
        var ownerId = Guid.Parse("019d9026-def4-74db-bf9a-f854c16a993e");
        await ProvisionTimelineAsync(ownerId);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = RegistrationRequest(ownerId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(7, Guid.Parse(json.RootElement.GetProperty("id").GetString()!).Version);
        Assert.Equal("heartbeat.collector.desktop.macos", json.RootElement.GetProperty("key").GetString());
        Assert.Equal("device-1", json.RootElement.GetProperty("target").GetString());
        Assert.Equal("My Mac", json.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(Now, json.RootElement.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task InvalidRegistrationReturnsProblemDetails()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = RegistrationRequest(ownerId, target: "   ");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task UnauthenticatedRequestReturnsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/collectors",
            new { key = "heartbeat.collector.desktop.macos", target = "device-1", displayName = "My Mac" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Test", response.Headers.WwwAuthenticate.Single().Scheme);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(401, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task MalformedJsonReturnsProblemDetails()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = RegistrationRequest(ownerId);
        request.Content = new StringContent("{\"key\":", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task RegistrationRejectsFieldsOutsideRequestDto()
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = RegistrationRequest(ownerId);
        request.Content = JsonContent.Create(new
        {
            key = "heartbeat.collector.desktop.macos",
            target = "device-1",
            displayName = "My Mac",
            id = Guid.NewGuid(),
            timelineId = Guid.NewGuid(),
            createdAt = Now,
            Track = new { },
        });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await using var db = CreateDbContext();
        Assert.Equal(0, await db.Collectors.CountAsync());
    }

    [Fact]
    public async Task OwnerWithoutTimelineReceivesStableConflict()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = RegistrationRequest(Guid.NewGuid());

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("timeline_not_provisioned", json.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public async Task MissingOrInvalidOwnerSubjectReturnsUnauthorized(string subject)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = RegistrationRequest(subject);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Heartbeat"] = ConnectionString,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<HeartbeatDbContext>>();
                services.RemoveAll<HeartbeatDbContext>();
                services.AddDbContext<HeartbeatDbContext>(options =>
                    options.UseNpgsql(ConnectionString));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
            });
        });

    private static HttpRequestMessage RegistrationRequest(
        Guid ownerId,
        string target = "device-1") => RegistrationRequest(ownerId.ToString(), target);

    private static HttpRequestMessage RegistrationRequest(
        string subject,
        string target = "device-1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/collectors")
        {
            Content = JsonContent.Create(new
            {
                key = "heartbeat.collector.desktop.macos",
                target,
                displayName = "My Mac",
            }),
        };
        request.Headers.Add(OwnerHeader, subject);
        return request;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(OwnerHeader, out var ownerValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = string.IsNullOrEmpty(ownerValues[0])
                ? []
                : new[] { new Claim("sub", ownerValues[0]!) };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Headers["WWW-Authenticate"] = SchemeName;
            return base.HandleChallengeAsync(properties);
        }
    }
}
