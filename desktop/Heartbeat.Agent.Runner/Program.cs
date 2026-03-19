using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Hosting;
using Heartbeat.Agent.Utils;
using Microsoft.Extensions.Hosting;
using Serilog;

// 配置 Serilog 日志
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Heartbeat", "logs");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logDir, "heartbeat-.log"),
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

using var guard = new SingleInstanceGuard();
if (!guard.IsFirstInstance)
{
    Log.Warning("Heartbeat 已在运行中，当前实例退出");
    await Log.CloseAndFlushAsync();
    return;
}

try
{
    var configManager = new ConfigManager();
    var config = configManager.Current;

    Log.Information("Heartbeat 客户端启动 (Console)");
    Log.Information("Base URL: {URL}", config.ApiBaseUrl);
    Log.Information("配置加载完成 - 上传间隔: {Upload}min, 状态间隔: {Status}s",
        config.UploadIntervalMinutes, config.StatusUploadIntervalSeconds);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();
    builder.Services.AddHeartbeatAgent(configManager, guard);

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "客户端异常终止");
}
finally
{
    await Log.CloseAndFlushAsync();
}