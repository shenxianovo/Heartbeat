using System.Text.Json;
using Orisumi.Api;
using Orisumi.Application.Entities;
using Orisumi.Core;
using Orisumi.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.AddOrisumiTelemetry();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 10 * 1024 * 1024);

var connectionString = builder.Configuration.GetConnectionString("Orisumi");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Orisumi must be configured.");
}

builder.Services.AddOrisumiInfrastructure(connectionString);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
});

var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    var problem = exception switch
    {
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            ApiProblem.Create(413, "The request body must not exceed 10 MiB."),
        EntityDeletedException => ApiProblem.Create(410, exception.Message),
        ResourceNotFoundException => ApiProblem.Create(404, exception.Message),
        ModelViolationException violation => ApiProblem.Create(ApiProblem.RuleStatus(violation.Rule), violation.Message, violation.Rule),
        BadHttpRequestException or JsonException =>
            ApiProblem.Create(400, "The request body is missing, malformed or does not match the contract."),
        _ => ApiProblem.Create(500, "The server could not complete the entity operation."),
    };
    await problem.ExecuteAsync(context);
}));
app.UseStatusCodePages(async context =>
{
    if (context.HttpContext.Response.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        await ApiProblem.Create(413, "The request body must not exceed 10 MiB.")
            .ExecuteAsync(context.HttpContext);
    }
});

app.MapEntities();
app.MapObservationDrafts();
app.Run();
