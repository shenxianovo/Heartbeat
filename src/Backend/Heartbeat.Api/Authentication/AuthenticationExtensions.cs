using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Heartbeat.Api.Authentication;

public static class AuthenticationExtensions
{
    private const string TokenSelectorScheme = "TokenSelector";
    private const string OidcBearerScheme = "OidcBearer";
    private const string SessionBearerScheme = "SessionBearer";

    public static IServiceCollection AddHeartbeatAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var auth = configuration.GetSection("AuthService");

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = TokenSelectorScheme;
                options.DefaultChallengeScheme = TokenSelectorScheme;
            })
            .AddPolicyScheme(
                TokenSelectorScheme,
                "Selects bearer scheme by JWT typ",
                options =>
                {
                    options.ForwardDefaultSelector = context =>
                    {
                        var authorization = context.Request.Headers.Authorization.ToString();
                        var token = authorization.StartsWith(
                            "Bearer ",
                            StringComparison.OrdinalIgnoreCase)
                            ? authorization["Bearer ".Length..]
                            : null;
                        return JwtTypeSniffer.IsOidcAccessToken(token)
                            ? OidcBearerScheme
                            : SessionBearerScheme;
                    };
                })
            .AddJwtBearer(OidcBearerScheme, options =>
            {
                options.Authority = auth["Authority"];
                options.RequireHttpsMetadata = !environment.IsDevelopment();
                options.MapInboundClaims = false;

                var audience = auth["OidcAudience"];
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = !string.IsNullOrEmpty(audience),
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = auth["OidcIssuer"],
                    ValidAudience = audience,
                    ValidTypes = ["at+jwt"],
                    NameClaimType = "preferred_username",
                    RoleClaimType = "role",
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var expectedClientId = auth["OidcClientId"];
                        var actualClientId = context.Principal?.FindFirst("client_id")?.Value;
                        if (!string.IsNullOrEmpty(expectedClientId)
                            && !string.Equals(
                                expectedClientId,
                                actualClientId,
                                StringComparison.Ordinal))
                        {
                            context.Fail("Access token was issued to a different client.");
                            return Task.CompletedTask;
                        }

                        return RequireOwnerIdAsync(context);
                    },
                };
            })
            .AddJwtBearer(SessionBearerScheme, options =>
            {
                options.Authority = auth["Authority"];
                options.RequireHttpsMetadata = !environment.IsDevelopment();
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = auth["Issuer"],
                    ValidAudience = auth["Audience"],
                    NameClaimType = "preferred_username",
                    RoleClaimType = "role",
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = RequireOwnerIdAsync,
                };
            });

        services.AddAuthorization();
        return services;
    }

    private static Task RequireOwnerIdAsync(TokenValidatedContext context)
    {
        if (!OwnerClaims.TryGetOwnerId(context.Principal, out _))
        {
            context.Fail("The token subject must be a non-empty UUID.");
        }

        return Task.CompletedTask;
    }
}
