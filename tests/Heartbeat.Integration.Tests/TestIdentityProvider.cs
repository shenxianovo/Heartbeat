using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Heartbeat.Integration.Tests;

/// <summary>
/// Self-signed stand-in for the real identity provider. It only mints tokens and publishes signing
/// keys; validation is left to the host's own registration (<c>AddHeartbeatAuthentication</c>), so
/// tests exercise the production JWT pipeline instead of an authentication test double.
/// </summary>
internal sealed class TestIdentityProvider : IDisposable
{
    public const string Authority = "https://idp.heartbeat.test";
    public const string OidcIssuer = "https://idp.heartbeat.test/oidc/";
    public const string OidcClientId = "heartbeat-web-test";
    public const string SessionIssuer = "https://idp.heartbeat.test/session";
    public const string SessionAudience = "heartbeat-agent-session";
    public const string UnexpectedIssuer = "https://attacker.heartbeat.test/";

    private readonly RSA _publishedKey = RSA.Create(2048);
    private readonly RSA _unpublishedKey = RSA.Create(2048);
    private readonly SigningCredentials _publishedCredentials;
    private readonly SigningCredentials _unpublishedCredentials;

    public TestIdentityProvider()
    {
        _publishedCredentials = Credentials(_publishedKey, "published-key");
        _unpublishedCredentials = Credentials(_unpublishedKey, "unpublished-key");
    }

    /// <summary>Key material the host is told to trust, published through the metadata stub.</summary>
    public SecurityKey PublishedSigningKey => _publishedCredentials.Key;

    /// <summary>Key material the host never learns about, used for signature-mismatch cases.</summary>
    public SigningCredentials UnpublishedCredentials => _unpublishedCredentials;

    /// <summary>
    /// Host configuration pointing <c>AuthService</c> at this stand-in. Keys that are not listed
    /// here keep their shipped <c>appsettings.json</c> values on purpose; most notably
    /// <c>OidcAudience</c>, whose empty default is the behaviour pinned by the audience tests.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Settings(string? oidcAudience = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AuthService:Authority"] = Authority,
            ["AuthService:Issuer"] = SessionIssuer,
            ["AuthService:Audience"] = SessionAudience,
            ["AuthService:OidcIssuer"] = OidcIssuer,
            ["AuthService:OidcClientId"] = OidcClientId,
        };

        if (oidcAudience is not null)
        {
            settings["AuthService:OidcAudience"] = oidcAudience;
        }

        return settings;
    }

    /// <summary>Mints an OIDC access token: header <c>typ</c> is <c>at+jwt</c>.</summary>
    public string CreateOidcAccessToken(
        string subject,
        string? issuer = null,
        string audience = "heartbeat-api",
        string? clientId = OidcClientId,
        SigningCredentials? credentials = null,
        DateTime? expires = null,
        string tokenType = "at+jwt")
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (clientId is not null)
        {
            claims["client_id"] = clientId;
        }

        return CreateToken(
            issuer ?? OidcIssuer,
            audience,
            claims,
            credentials ?? _publishedCredentials,
            expires,
            tokenType);
    }

    /// <summary>Mints an agent session token: header <c>typ</c> is the default <c>JWT</c>.</summary>
    public string CreateSessionToken(
        string subject,
        string? issuer = null,
        string? audience = null,
        SigningCredentials? credentials = null,
        DateTime? expires = null,
        string tokenType = "JWT") =>
        CreateToken(
            issuer ?? SessionIssuer,
            audience ?? SessionAudience,
            new Dictionary<string, object> { ["sub"] = subject },
            credentials ?? _publishedCredentials,
            expires,
            tokenType);

    public void Dispose()
    {
        _publishedKey.Dispose();
        _unpublishedKey.Dispose();
    }

    private static string CreateToken(
        string issuer,
        string audience,
        Dictionary<string, object> claims,
        SigningCredentials credentials,
        DateTime? expires,
        string tokenType)
    {
        var expiresAt = expires ?? DateTime.UtcNow.AddHours(1);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            SigningCredentials = credentials,
            TokenType = tokenType,
            IssuedAt = expiresAt.AddHours(-1),
            NotBefore = expiresAt.AddHours(-1),
            Expires = expiresAt,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static SigningCredentials Credentials(RSA key, string keyId) =>
        new(new RsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
}

/// <summary>
/// Replaces only the discovery document of every registered JwtBearer scheme, keeping each scheme's
/// own <see cref="TokenValidationParameters"/> and events exactly as the host configured them. The
/// trusted issuer is read back from the host options, so the schemes stay the single source of truth.
/// </summary>
internal sealed class TestIdentityProviderMetadata(TestIdentityProvider identityProvider)
    : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = options.TokenValidationParameters.ValidIssuer,
        };
        configuration.SigningKeys.Add(identityProvider.PublishedSigningKey);
        options.ConfigurationManager =
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
    }
}
