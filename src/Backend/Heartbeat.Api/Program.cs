using Heartbeat.Api.Serialization;
using Heartbeat.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Heartbeat");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Heartbeat must be configured.");
}

builder.Services.AddHeartbeatInfrastructure(connectionString);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.Converters.Add(new EntityIdJsonConverter());
    options.SerializerOptions.Converters.Add(new UtcDateTimeOffsetJsonConverter());
});

var app = builder.Build();

app.Run();
