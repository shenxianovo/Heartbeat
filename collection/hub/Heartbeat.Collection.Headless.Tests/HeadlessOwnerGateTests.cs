using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Heartbeat.Collection.Headless.Tests;

/// <summary>
/// The cross-owner gate itself, exercised through the host's real bearer configuration rather than
/// through a stand-in handler: <see cref="HeadlessOwnerAuthentication.AddHeadlessOwnerAuthentication" />
/// is the same call <c>Program</c> makes, so what these tests refuse is what the Headless Hub refuses.
///
/// The point is the question the gate asks beyond "is this token valid": a token this authority really
/// signed, with a real lifetime and the expected <c>at+jwt</c> type, is still refused unless its
/// <c>sub</c> is this Hub's owner and its <c>client_id</c> is the expected client. Only the signing key
/// is the test's own — the Hub takes its keys from OIDC discovery, which is the host's deployment
/// concern and not what "may this owner manage this Hub" means.
/// </summary>
public sealed class HeadlessOwnerGateTests : IAsyncLifetime
{
    private const string PackageUpdatePath = "/hub/api/v1/collector-instances";
    private const string OwnerSubject = "owner-1";
    private const string ClientId = "heartbeat-web";
    private const string Issuer = "https://auth.example.test/";

    private readonly SymmetricSecurityKey _signingKey =
        new(RandomNumberGenerator.GetBytes(64)) { KeyId = "test-signing-key" };
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-headless-owner-gate-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private CollectorRuntime _runtime = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _runtime = CollectorRuntime.Open(
            Path.Combine(_directory, "collector-runtime.json"),
            new DiscardingSegmentSink());

        var management = new HeadlessManagementOptions
        {
            OwnerSubject = OwnerSubject,
            Authority = "https://auth.example.test",
            Issuer = Issuer,
            ClientId = ClientId
        };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddHeadlessOwnerAuthentication(management);
        // The only test-side change to the host's configuration: keys come from this test instead of from
        // the authority's discovery document, so no assertion here depends on a network round trip.
        builder.Services.PostConfigure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options =>
            {
                options.Authority = null;
                options.ConfigurationManager = null;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.IssuerSigningKey = _signingKey;
            });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new HeadlessFleetManager(new HeadlessFleetOptions
        {
            ApiKey = "test-key",
            DataDirectory = _directory,
            Management = management,
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
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _runtime.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// Another person's valid token is not a weaker owner: authorization here is identity, so every
    /// Collector Package update endpoint answers 401 rather than acting on someone else's Hub.
    /// </summary>
    [Fact]
    public async Task PackageUpdateEndpoints_TokenFromAnotherSubject_AreRefused()
    {
        var responses = await CallEveryEndpointAsync(Token(subject: "owner-2", clientId: ClientId));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    /// <summary>
    /// The owner's own <c>sub</c> presented by a client this Hub does not expect is refused as well: the
    /// gate is the pair, not just the person.
    /// </summary>
    [Fact]
    public async Task PackageUpdateEndpoints_TokenFromAnotherClient_AreRefused()
    {
        var responses = await CallEveryEndpointAsync(Token(subject: OwnerSubject, clientId: "some-other-app"));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    /// <summary>
    /// The mirror image, so the refusals above cannot be explained by a gate that rejects everything: the
    /// Hub owner's own token passes authentication and reaches the endpoints, which then answer about the
    /// Collector Instance — absent here, because this Hub runs none.
    /// </summary>
    [Fact]
    public async Task PackageUpdateEndpoints_TokenFromTheHubOwner_ReachTheCollectorInstanceLookup()
    {
        var responses = await CallEveryEndpointAsync(Token(subject: OwnerSubject, clientId: ClientId));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    private async Task<IReadOnlyList<HttpResponseMessage>> CallEveryEndpointAsync(string token)
    {
        var collectorInstanceId = Guid.CreateVersion7();
        var instance = $"{PackageUpdatePath}/{collectorInstanceId:D}/package-update";
        var responses = new List<HttpResponseMessage>
        {
            await SendAsync(new HttpRequestMessage(HttpMethod.Get, instance), token),
            await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{instance}/check"), token),
            await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{instance}/switch"), token),
            await SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"{instance}/approval")
                {
                    Content = JsonContent.Create(new CollectorPackageApprovalRequest(
                        "com.example.collector",
                        "1.0.0",
                        new string('a', 64)))
                },
                token)
        };
        return responses;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    /// <summary>An access token this Hub's authority would issue, for the given owner and client.</summary>
    private string Token(string subject, string clientId) => new JsonWebTokenHandler().CreateToken(
        new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            TokenType = "at+jwt",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["client_id"] = clientId
            },
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
        });

    private sealed class DiscardingSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }
}
