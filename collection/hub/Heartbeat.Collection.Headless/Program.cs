using Heartbeat.Collection.Headless;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text.Json.Serialization;

var configPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "heartbeat-headless.json");
var options = HeadlessFleetOptions.Load(configPath);
options.Validate();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls(options.ListenUrl);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<HeadlessFleetManager>();
    builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<HeadlessFleetManager>());
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(authentication =>
        {
            authentication.Authority = options.Management.Authority;
            authentication.RequireHttpsMetadata = options.Management.RequireHttpsMetadata;
            authentication.MapInboundClaims = false;
            authentication.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = !string.IsNullOrWhiteSpace(options.Management.Audience),
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = options.Management.Issuer,
                ValidAudience = options.Management.Audience,
                ValidTypes = ["at+jwt"],
                NameClaimType = "preferred_username"
            };
            authentication.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var clientId = context.Principal?.FindFirst("client_id")?.Value;
                    var subject = context.Principal?.FindFirst("sub")?.Value;
                    if (clientId != options.Management.ClientId || subject != options.Management.OwnerSubject)
                        context.Fail("Token does not belong to this Hub owner and client.");
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();
    builder.Services.ConfigureHttpJsonOptions(json =>
        json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();

    var management = app.MapGroup("/hub/api/v1").RequireAuthorization();
    management.MapGet(
        "/collectors",
        async (HeadlessFleetManager fleet, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await fleet.BrowseAsync(cancellationToken)); }
            catch (HttpRequestException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    management.MapPost(
        "/collectors/{packageId}/installation",
        async (string packageId, HeadlessFleetManager fleet, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await fleet.InstallAsync(packageId, cancellationToken)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception exception) when (exception is
                HttpRequestException or
                Heartbeat.Collection.Hub.Collectors.Packages.PackageValidationException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
    management.MapDelete(
        "/collectors/{packageId}/installation",
        async (string packageId, HeadlessFleetManager fleet, CancellationToken cancellationToken) =>
        {
            try
            {
                await fleet.UninstallAsync(packageId, cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
    management.MapPost(
        "/collectors/{packageId}/activation",
        async (string packageId, HeadlessFleetManager fleet, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await fleet.RetryActivationAsync(packageId, cancellationToken)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
    management.MapPost(
        "/collector-instances/{collectorInstanceId:guid}/authorization/{interactionId:guid}",
        async (Guid collectorInstanceId, Guid interactionId, AuthorizationResponse request, HeadlessFleetManager fleet, CancellationToken cancellationToken) =>
        {
            try
            {
                await fleet.SubmitAuthorizationAsync(collectorInstanceId, interactionId, request.Values, cancellationToken);
                return Results.Accepted();
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

    await app.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

public sealed record AuthorizationResponse(IReadOnlyDictionary<string, string> Values);
