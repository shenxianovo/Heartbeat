using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Heartbeat.Agent.Mac.Hosting;
using Heartbeat.Desktop.UI.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Desktop.Mac;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var logFeed = new RingBufferSink(200);
        ConfigureLogging(logFeed);
        RegisterUnhandledExceptionLogging();

        using var guard = new MacSingleInstanceGuard();
        if (!guard.IsFirstInstance)
        {
            Log.Warning("Heartbeat 已在运行中，当前实例退出");
            Log.CloseAndFlush();
            return;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSerilog();
        builder.Services.AddSingleton<IMacLoginStart, LaunchAgentLoginStart>();
        builder.Services.AddHeartbeatMacAgent();
        builder.Services.AddSingleton<MacDesktopState>();
        using var host = builder.Build();

        host.StartAsync().GetAwaiter().GetResult();
        var runtime = new MacDesktopRuntime(
            host,
            host.Services.GetRequiredService<MacDesktopState>(),
            logFeed);
        App.Runtime = runtime;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();

    private static void ConfigureLogging(RingBufferSink sink)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Heartbeat", "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(logDirectory, "heartbeat-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.RingBuffer(sink)
            .CreateLogger();
    }

    private static void RegisterUnhandledExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                Log.Fatal(exception, "未处理的域异常");
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "未观察的 Task 异常");
            eventArgs.SetObserved();
        };
    }
}
