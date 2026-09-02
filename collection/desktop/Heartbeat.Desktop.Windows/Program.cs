using Avalonia;
using Avalonia.Controls;
using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Hosting;
using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Desktop.UI.Diagnostics;
using Heartbeat.Desktop.UI.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Velopack;

namespace Heartbeat.Desktop.Windows;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var logSink = new RingBufferSink(200);
        ConfigureLogging(logSink);
        RegisterUnhandledExceptionLogging();

        var smokeRequested = DesktopStartupSmoke.TryGetRequest(args, out var smoke);
        using var guard = new SingleInstanceGuard();
        if (!guard.IsFirstInstance)
        {
            Log.Warning("Heartbeat 已在运行中，当前实例退出");
            if (smokeRequested)
                Environment.ExitCode = DesktopStartupSmoke.Inconclusive(
                    smoke, "another instance already holds the single-instance guard");
            else
                WindowsMessageBox.ShowAlreadyRunning();
            Log.CloseAndFlush();
            return;
        }

        var config = new ConfigManager();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSerilog();
        builder.Services.AddHeartbeatAgent(config, guard);
        using var host = builder.Build();

        // 发布产物的启动 smoke：只起停 host，不拉起 UI（ADR-048 不变量 4 的门禁）。
        if (smokeRequested)
        {
            Environment.ExitCode = DesktopStartupSmoke.Run(host, smoke);
            Log.CloseAndFlush();
            return;
        }

        host.StartAsync().GetAwaiter().GetResult();
        var runtime = new WindowsDesktopRuntime(host, guard, config, logSink);
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
        AppBuilder.Configure<App>()
            .UsePlatformDetect();

    private static void ConfigureLogging(RingBufferSink sink)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Heartbeat",
            "logs");

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
