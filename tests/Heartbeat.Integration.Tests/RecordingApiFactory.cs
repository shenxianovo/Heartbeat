using System.Security.Claims;
using System.Text.Encodings.Web;
using Heartbeat.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

internal static class RecordingApiFactory
{
    public const string OwnerHeader = "X-Test-Owner";

    /// <summary>
    /// Host with the authentication schemes replaced by a header-driven test handler. Use this for
    /// endpoint authorization and owner-scoping coverage; it proves nothing about JWT validation.
    /// </summary>
    public static WebApplicationFactory<Program> Create(string connectionString, TimeProvider timeProvider) =>
        Create(
            connectionString,
            timeProvider,
            settings: null,
            configureAuthentication: services => services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    _ => { }));

    /// <summary>
    /// Host running its own authentication registration (<c>AddHeartbeatAuthentication</c>) with the
    /// real JWT validation pipeline. Only the identity provider's discovery document is stubbed, so
    /// signature, issuer, lifetime, type and audience checks are the production ones.
    /// </summary>
    public static WebApplicationFactory<Program> CreateWithRealAuthentication(
        string connectionString,
        TimeProvider timeProvider,
        TestIdentityProvider identityProvider,
        string? oidcAudience = null) =>
        Create(
            connectionString,
            timeProvider,
            TestIdentityProvider.Settings(oidcAudience),
            configureAuthentication: services => services
                .AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(
                    new TestIdentityProviderMetadata(identityProvider)));

    private static WebApplicationFactory<Program> Create(
        string connectionString,
        TimeProvider timeProvider,
        IReadOnlyDictionary<string, string?>? settings,
        Action<IServiceCollection> configureAuthentication) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Heartbeat"] = connectionString,
                };
                foreach (var setting in settings ?? new Dictionary<string, string?>())
                {
                    values[setting.Key] = setting.Value;
                }

                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<HeartbeatDbContext>>();
                services.RemoveAll<HeartbeatDbContext>();
                services.AddDbContext<HeartbeatDbContext>(options =>
                    options.UseNpgsql(connectionString));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(timeProvider);
                configureAuthentication(services);
            });
        });

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
