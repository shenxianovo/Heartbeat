using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Heartbeat.Collection.Headless.Tests;

/// <summary>
/// The HTTP shape of the Collector Package update surface. What matters here is not the delivery
/// mechanics — those are the Hub's own tests — but that the three owner commands are reachable only
/// through the Hub's existing authenticated management API, and that they answer over the same Hub
/// Runtime rather than through a second surface of their own.
///
/// The host is the real one: the endpoints under test are mapped by the same
/// <see cref="HeadlessManagementApi.MapHeadlessManagementApi" /> the Headless Hub calls, so an
/// endpoint accidentally mapped outside the authorized group would fail these tests.
/// </summary>
public sealed class HeadlessManagementApiTests : IAsyncLifetime
{
    private const string PackageUpdatePath = "/hub/api/v1/collector-instances";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-headless-api-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private CollectorRuntime _runtime = null!;
    private HttpClient _anonymous = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _runtime = CollectorRuntime.Open(
            Path.Combine(_directory, "collector-runtime.json"),
            new DiscardingSegmentSink());

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddAuthentication(OwnerAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, OwnerAuthenticationHandler>(
                OwnerAuthenticationHandler.SchemeName,
                configureOptions: null);
        builder.Services.AddAuthorization();
        // The fleet is never started here: these tests are about the API surface, and the subject
        // endpoint only needs the same registration the Headless host makes.
        builder.Services.AddSingleton(new HeadlessFleetManager(new HeadlessFleetOptions
        {
            ApiKey = "test-key",
            DataDirectory = _directory,
            Management = new HeadlessManagementOptions
            {
                OwnerSubject = "owner-1",
                Authority = "https://auth.example.test",
                Issuer = "https://auth.example.test/",
                ClientId = "heartbeat-web"
            },
            Instances = []
        }));
        builder.Services.AddSingleton(new CollectorPackageUpdateService(
            _runtime,
            new CollectorInstallationStore(_directory)));

        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapHeadlessManagementApi();
        await _app.StartAsync();

        var baseAddress = new Uri(_app.Urls.First());
        _anonymous = new HttpClient { BaseAddress = baseAddress };
        _owner = new HttpClient { BaseAddress = baseAddress };
        _owner.DefaultRequestHeaders.Add(OwnerAuthenticationHandler.OwnerHeader, "yes");
    }

    public async Task DisposeAsync()
    {
        _anonymous.Dispose();
        _owner.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _runtime.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// The gate is structural, not per-endpoint discipline: the update commands live in the same
    /// authorized group as the management endpoints that existed before them, so none of them can
    /// be reached without the Hub owner's credentials.
    /// </summary>
    [Fact]
    public void ManagementApi_PutsEveryEndpointBehindTheExistingAuthorizationGate()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
        {
            Assert.StartsWith(
                HeadlessManagementApi.BasePath,
                endpoint.RoutePattern.RawText,
                StringComparison.Ordinal);
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        });

        var patterns = endpoints.Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
        Assert.Contains(
            "/hub/api/v1/collector-instances/{collectorInstanceId:guid}/package-update",
            patterns);
        Assert.Contains(
            "/hub/api/v1/collector-instances/{collectorInstanceId:guid}/package-update/check",
            patterns);
        Assert.Contains(
            "/hub/api/v1/collector-instances/{collectorInstanceId:guid}/package-update/approval",
            patterns);
    }

    [Fact]
    public async Task PackageUpdateEndpoints_RejectUnauthenticatedCallers()
    {
        var collectorInstanceId = Guid.CreateVersion7();

        var current = await _anonymous.GetAsync($"{PackageUpdatePath}/{collectorInstanceId:D}/package-update");
        var check = await _anonymous.PostAsync(
            $"{PackageUpdatePath}/{collectorInstanceId:D}/package-update/check",
            content: null);
        var approval = await _anonymous.PostAsJsonAsync(
            $"{PackageUpdatePath}/{collectorInstanceId:D}/package-update/approval",
            new CollectorPackageApprovalRequest("com.example.collector", "1.0.0", new string('a', 64)));

        Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, check.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, approval.StatusCode);
    }

    /// <summary>
    /// An authenticated owner still only reaches the Collector Instances this Hub actually runs;
    /// an unknown one is absent rather than an error to interpret.
    /// </summary>
    [Fact]
    public async Task PackageUpdateEndpoints_ReportUnknownCollectorInstanceAsNotFound()
    {
        var collectorInstanceId = Guid.CreateVersion7();

        var current = await _owner.GetAsync($"{PackageUpdatePath}/{collectorInstanceId:D}/package-update");
        var check = await _owner.PostAsync(
            $"{PackageUpdatePath}/{collectorInstanceId:D}/package-update/check",
            content: null);
        var approval = await _owner.PostAsJsonAsync(
            $"{PackageUpdatePath}/{collectorInstanceId:D}/package-update/approval",
            new CollectorPackageApprovalRequest("com.example.collector", "1.0.0", new string('a', 64)));

        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, approval.StatusCode);
    }

    private sealed class DiscardingSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }

    /// <summary>
    /// Stands in for the host's OIDC bearer configuration. It only answers the question the API
    /// itself asks — "is this the authenticated Hub owner?" — so these tests exercise the route
    /// group's gate without re-testing token validation, which the host configures once.
    /// </summary>
    private sealed class OwnerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestOwner";
        public const string OwnerHeader = "X-Test-Owner";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(OwnerHeader, out StringValues value) || value != "yes")
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner-1")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
