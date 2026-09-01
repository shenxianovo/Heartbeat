using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Heartbeat.Collection.Headless;

/// <summary>
/// The Headless Hub's single answer to "is this request the Hub owner?". It is the existing OIDC bearer
/// configuration, kept in one named place so the host wires it and tests exercise it through the same
/// code path: a token this Hub's authority signed still only counts when it carries this Hub's owner
/// <c>sub</c> and the expected <c>client_id</c>. No Collector Package update endpoint has a gate of its
/// own, and there is no second scheme, token or permission model behind them.
/// </summary>
public static class HeadlessOwnerAuthentication
{
    public static AuthenticationBuilder AddHeadlessOwnerAuthentication(
        this IServiceCollection services,
        HeadlessManagementOptions management)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(management);

        return services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(authentication => Configure(authentication, management));
    }

    private static void Configure(JwtBearerOptions authentication, HeadlessManagementOptions management)
    {
        authentication.Authority = management.Authority;
        authentication.RequireHttpsMetadata = management.RequireHttpsMetadata;
        authentication.MapInboundClaims = false;
        authentication.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = !string.IsNullOrWhiteSpace(management.Audience),
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = management.Issuer,
            ValidAudience = management.Audience,
            ValidTypes = ["at+jwt"],
            NameClaimType = "preferred_username"
        };
        authentication.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var clientId = context.Principal?.FindFirst("client_id")?.Value;
                var subject = context.Principal?.FindFirst("sub")?.Value;
                if (clientId != management.ClientId || subject != management.OwnerSubject)
                    context.Fail("Token does not belong to this Hub owner and client.");
                return Task.CompletedTask;
            }
        };
    }
}
