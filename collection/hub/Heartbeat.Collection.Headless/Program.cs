using Heartbeat.Collection.Headless;
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
    // The Hub Runtime the update surface reads is the one the fleet already owns, so the API cannot
    // observe or write a second copy of the Collector Instance's state.
    builder.Services.AddSingleton(provider =>
        provider.GetRequiredService<HeadlessFleetManager>().PackageUpdates);
    builder.Services.AddHeadlessOwnerAuthentication(options.Management);
    builder.Services.AddAuthorization();
    builder.Services.ConfigureHttpJsonOptions(json =>
        json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHeadlessManagementApi();

    await app.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
