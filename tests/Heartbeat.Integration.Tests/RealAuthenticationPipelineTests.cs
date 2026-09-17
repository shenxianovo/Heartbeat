using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Integration.Tests;

/// <summary>
/// Covers the host's own authentication registration end to end: tokens are minted by a self-signed
/// in-memory identity provider, but every check (signature, issuer, lifetime, token type, audience,
/// client and owner claims) is performed by the production pipeline, not by a test handler.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RealAuthenticationPipelineTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 12, 9, 30, 0, TimeSpan.Zero);

    /// <summary>Which of the host's two bearer schemes a token is minted for.</summary>
    public enum Scheme
    {
        /// <summary>OIDC access token, header <c>typ</c> is <c>at+jwt</c>.</summary>
        OidcAccess,

        /// <summary>Agent session token, header <c>typ</c> is <c>JWT</c>.</summary>
        Session,
    }

    [Theory]
    [InlineData(Scheme.OidcAccess)]
    [InlineData(Scheme.Session)]
    public async Task ValidTokenOfEitherSchemeAuthenticatesOwner(Scheme scheme)
    {
        var ownerId = Guid.CreateVersion7();
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(
            RegistrationRequest(Mint(identityProvider, scheme, ownerId.ToString())));

        await AssertOwnerRegisteredAsync(response, ownerId);
    }

    [Fact]
    public async Task TokenFromUnexpectedIssuerIsRejected()
    {
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();
        var oidcToken = identityProvider.CreateOidcAccessToken(
            Guid.CreateVersion7().ToString(),
            issuer: TestIdentityProvider.UnexpectedIssuer);
        var sessionToken = identityProvider.CreateSessionToken(
            Guid.CreateVersion7().ToString(),
            issuer: TestIdentityProvider.UnexpectedIssuer);

        using var oidcResponse = await client.SendAsync(RegistrationRequest(oidcToken));
        using var sessionResponse = await client.SendAsync(RegistrationRequest(sessionToken));

        AssertBearerChallenge(oidcResponse, "The issuer");
        AssertBearerChallenge(sessionResponse, "The issuer");
        await AssertNoTimelineAsync();
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();
        var expired = DateTime.UtcNow.AddHours(-1);
        var oidcToken = identityProvider.CreateOidcAccessToken(
            Guid.CreateVersion7().ToString(),
            expires: expired);
        var sessionToken = identityProvider.CreateSessionToken(
            Guid.CreateVersion7().ToString(),
            expires: expired);

        using var oidcResponse = await client.SendAsync(RegistrationRequest(oidcToken));
        using var sessionResponse = await client.SendAsync(RegistrationRequest(sessionToken));

        AssertBearerChallenge(oidcResponse, "expired");
        AssertBearerChallenge(sessionResponse, "expired");
        await AssertNoTimelineAsync();
    }

    [Fact]
    public async Task TokenSignedWithUnpublishedKeyIsRejected()
    {
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();
        var oidcToken = identityProvider.CreateOidcAccessToken(
            Guid.CreateVersion7().ToString(),
            credentials: identityProvider.UnpublishedCredentials);
        var sessionToken = identityProvider.CreateSessionToken(
            Guid.CreateVersion7().ToString(),
            credentials: identityProvider.UnpublishedCredentials);

        using var oidcResponse = await client.SendAsync(RegistrationRequest(oidcToken));
        using var sessionResponse = await client.SendAsync(RegistrationRequest(sessionToken));

        AssertBearerChallenge(oidcResponse, "signature");
        AssertBearerChallenge(sessionResponse, "signature");
        await AssertNoTimelineAsync();
    }

    [Fact]
    public async Task AccessTokenTypeHeaderRoutesToTheOidcScheme()
    {
        var ownerId = Guid.CreateVersion7().ToString();
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();

        // Same session claims twice; only the header "typ" differs, so only the routing changes.
        using var routedToSession = await client.SendAsync(
            RegistrationRequest(identityProvider.CreateSessionToken(ownerId)));
        using var routedToOidc = await client.SendAsync(
            RegistrationRequest(identityProvider.CreateSessionToken(ownerId, tokenType: "at+jwt")));

        Assert.Equal(HttpStatusCode.OK, routedToSession.StatusCode);
        AssertBearerChallenge(routedToOidc, TestIdentityProvider.SessionIssuer);
    }

    [Fact]
    public async Task PlainJwtTypeHeaderRoutesToTheSessionScheme()
    {
        var ownerId = Guid.CreateVersion7().ToString();
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();

        // Same OIDC claims twice; with "typ":"JWT" the session scheme validates the token and
        // rejects its audience. Only the session scheme validates audience at all (the OIDC scheme
        // has it switched off, see the audience tests below), so that failure identifies the scheme.
        using var routedToOidc = await client.SendAsync(
            RegistrationRequest(identityProvider.CreateOidcAccessToken(ownerId)));
        using var routedToSession = await client.SendAsync(
            RegistrationRequest(identityProvider.CreateOidcAccessToken(ownerId, tokenType: "JWT")));

        Assert.Equal(HttpStatusCode.OK, routedToOidc.StatusCode);
        AssertBearerChallenge(routedToSession, "The audience");
    }

    /// <summary>
    /// With audience validation off (see below), <c>client_id</c> is the only check on who the access
    /// token was issued to, so both a foreign and a missing value must be rejected.
    /// </summary>
    [Theory]
    [InlineData("someone-elses-app")]
    [InlineData(null)]
    public async Task AccessTokenNotIssuedToTheConfiguredClientIsRejected(string? clientId)
    {
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();
        var token = identityProvider.CreateOidcAccessToken(
            Guid.CreateVersion7().ToString(),
            clientId: clientId);

        using var response = await client.SendAsync(RegistrationRequest(token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoTimelineAsync();
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task VerifiedTokenWithoutUuidSubjectIsRejected(string subject)
    {
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();

        using var oidcResponse = await client.SendAsync(
            RegistrationRequest(identityProvider.CreateOidcAccessToken(subject)));
        using var sessionResponse = await client.SendAsync(
            RegistrationRequest(identityProvider.CreateSessionToken(subject)));

        Assert.Equal(HttpStatusCode.Unauthorized, oidcResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, sessionResponse.StatusCode);
        await AssertNoTimelineAsync();
    }

    [Fact]
    public async Task SessionTokenWithWrongAudienceIsRejected()
    {
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();
        var token = identityProvider.CreateSessionToken(
            Guid.CreateVersion7().ToString(),
            audience: "some-other-api");

        using var response = await client.SendAsync(RegistrationRequest(token));

        AssertBearerChallenge(response, "The audience");
        await AssertNoTimelineAsync();
    }

    /// <summary>
    /// Pins current behaviour, not desired behaviour: <c>appsettings.json</c> ships an empty
    /// <c>AuthService:OidcAudience</c>, so <c>AddHeartbeatAuthentication</c> silently turns audience
    /// validation off and an access token minted for a different API is accepted. Issue `S-02`; the
    /// real identity provider's audience value is unknown, so the fix is a pending decision.
    /// </summary>
    [Fact]
    public async Task AudienceIsNotValidatedWhenOidcAudienceIsNotConfiguredWhichIsCurrentBehaviourPendingDecision()
    {
        var ownerId = Guid.CreateVersion7();
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider);
        using var client = factory.CreateClient();
        Assert.Equal(
            string.Empty,
            factory.Services.GetRequiredService<IConfiguration>()["AuthService:OidcAudience"]);

        using var response = await client.SendAsync(RegistrationRequest(
            identityProvider.CreateOidcAccessToken(
                ownerId.ToString(),
                audience: "an-entirely-different-api")));

        await AssertOwnerRegisteredAsync(response, ownerId);
    }

    [Fact]
    public async Task AudienceIsValidatedOnceOidcAudienceIsConfigured()
    {
        const string audience = "heartbeat-api";
        using var identityProvider = new TestIdentityProvider();
        await using var factory = CreateFactory(identityProvider, oidcAudience: audience);
        using var client = factory.CreateClient();
        var ownerId = Guid.CreateVersion7();

        using var accepted = await client.SendAsync(RegistrationRequest(
            identityProvider.CreateOidcAccessToken(ownerId.ToString(), audience: audience)));
        using var rejected = await client.SendAsync(RegistrationRequest(
            identityProvider.CreateOidcAccessToken(
                Guid.CreateVersion7().ToString(),
                audience: "an-entirely-different-api")));

        AssertBearerChallenge(rejected, "The audience");
        await AssertOwnerRegisteredAsync(accepted, ownerId);
    }

    private static string Mint(TestIdentityProvider identityProvider, Scheme scheme, string subject) =>
        scheme switch
        {
            Scheme.OidcAccess => identityProvider.CreateOidcAccessToken(subject),
            Scheme.Session => identityProvider.CreateSessionToken(subject),
            _ => throw new ArgumentOutOfRangeException(nameof(scheme)),
        };

    private async Task AssertOwnerRegisteredAsync(HttpResponseMessage response, Guid ownerId)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = CreateDbContext();
        Assert.Equal(ownerId, (await db.Timelines.SingleAsync()).OwnerId);
    }

    private static void AssertBearerChallenge(HttpResponseMessage response, string expectedDetail)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.Single();
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains(expectedDetail, challenge.Parameter ?? string.Empty, StringComparison.Ordinal);
    }

    private async Task AssertNoTimelineAsync()
    {
        await using var db = CreateDbContext();
        Assert.Empty(await db.Timelines.ToListAsync());
    }

    private WebApplicationFactory<Program> CreateFactory(
        TestIdentityProvider identityProvider,
        string? oidcAudience = null) =>
        RecordingApiFactory.CreateWithRealAuthentication(
            ConnectionString,
            new FixedTimeProvider(Now),
            identityProvider,
            oidcAudience);

    private static HttpRequestMessage RegistrationRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/collectors")
        {
            Content = JsonContent.Create(new
            {
                key = "heartbeat.collector.desktop.macos",
                target = "device-1",
                displayName = "My Mac",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
