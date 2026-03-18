using System.Windows;
using H.NotifyIcon;
using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Hosting;
using Heartbeat.WPF.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Application = System.Windows.Application;

namespace Heartbeat.WPF
{
    public partial class App : Application
    {
        private IHost? _host;
        private TaskbarIcon? _trayIcon;
        private MainWindow? _mainWindow;

        public static RingBufferSink LogSink { get; } = new(200);
        public static ConfigManager ConfigManager { get; private set; } = null!;
        public static IServiceProvider Services => ((App)Current)._host!.Services;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 初始化配置管理器
            ConfigManager = new ConfigManager();

            // 配置日志
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heartbeat", "logs");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(System.IO.Path.Combine(logDir, "heartbeat-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.RingBuffer(LogSink)
                .CreateLogger();

            Log.Information("Heartbeat WPF 客户端启动");

            // 构建 Host
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSerilog();
            builder.Services.AddHeartbeatAgent(ConfigManager);

            _host = builder.Build();

            // 初始化托盘图标
            InitializeTrayIcon();

            // 启动后台服务
            await _host.StartAsync();

            Log.Information("后台服务已启动");
        }

        private void InitializeTrayIcon()
        {
            // 加载嵌入资源图标
            var iconUri = new Uri("pack://application:,,,/heartbeat.ico");
            var iconStream = GetResourceStream(iconUri)?.Stream;
            var icon = iconStream != null
                ? new System.Drawing.Icon(iconStream)
                : System.Drawing.SystemIcons.Application;

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "Heartbeat",
                Icon = icon,
                ContextMenu = CreateContextMenu(),
                Visibility = Visibility.Visible,
            };

            _trayIcon.ForceCreate();
            _trayIcon.TrayLeftMouseDown += (_, _) => ShowMainWindow();
        }

        private System.Windows.Controls.ContextMenu CreateContextMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var openItem = new System.Windows.Controls.MenuItem { Header = "打开主界面" };
            openItem.Click += (_, _) => ShowMainWindow();
            menu.Items.Add(openItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
            exitItem.Click += async (_, _) => await ExitApplicationAsync();
            menu.Items.Add(exitItem);

            return menu;
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null || !_mainWindow.IsLoaded)
            {
                _mainWindow = new MainWindow();
            }

            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        private async Task ExitApplicationAsync()
        {
            Log.Information("正在退出应用...");

            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }

            _trayIcon?.Dispose();
            await Log.CloseAndFlushAsync();

            Shutdown();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }

            _trayIcon?.Dispose();
            await Log.CloseAndFlushAsync();

            base.OnExit(e);
        }
    }
}
