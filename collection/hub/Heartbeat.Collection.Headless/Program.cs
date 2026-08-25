using Heartbeat.Collection.Headless;
using Microsoft.Extensions.Hosting;
using Serilog;

var configPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "heartbeat-headless.json");
var options = HeadlessHubOptions.Load(configPath);
options.Validate();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddHeartbeatHeadlessHub(options);
    using var host = builder.Build();
    await host.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
