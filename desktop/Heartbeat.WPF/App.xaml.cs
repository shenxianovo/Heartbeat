using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using H.NotifyIcon;
using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Hosting;
using Heartbeat.Agent.Utils;
using Heartbeat.WPF.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Application = System.Windows.Application;

namespace Heartbeat.WPF
{
    public partial class App : Application
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        private IHost? _host;
        private TaskbarIcon? _trayIcon;
        private MainWindow? _mainWindow;
        private SingleInstanceGuard? _guard;
        private HwndSource? _msgWindow;
        private uint _taskbarCreatedMsg;

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

            // 单实例检查
            _guard = new SingleInstanceGuard();
            if (!_guard.IsFirstInstance)
            {
                Log.Warning("Heartbeat 已在运行中，当前实例退出");
                MessageBox.Show("Heartbeat 已在运行中。", "Heartbeat", MessageBoxButton.OK, MessageBoxImage.Information);
                _guard.Dispose();
                await Log.CloseAndFlushAsync();
                Shutdown();
                return;
            }

            Log.Information("Heartbeat WPF 客户端启动");

            // 构建 Host
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSerilog();
            builder.Services.AddHeartbeatAgent(ConfigManager, _guard);

            _host = builder.Build();

            // 初始化托盘图标
            InitializeTrayIcon();

            // 监听 TaskbarCreated 消息，当 explorer.exe 重启时恢复托盘图标
            RegisterTaskbarCreatedHandler();

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

        /// <summary>
        /// 注册 TaskbarCreated 消息监听，当 explorer.exe 重启时恢复托盘图标
        /// </summary>
        private void RegisterTaskbarCreatedHandler()
        {
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

            var parameters = new HwndSourceParameters("HeartbeatTaskbarWatcher")
            {
                Width = 0,
                Height = 0,
                PositionX = -100,
                PositionY = -100,
                WindowStyle = 0,
            };
            _msgWindow = new HwndSource(parameters);
            _msgWindow.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
            {
                Log.Information("检测到任务栏重建（explorer.exe 重启），正在恢复托盘图标...");
                RecreateTrayIcon();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void RecreateTrayIcon()
        {
            try
            {
                _trayIcon?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "释放旧托盘图标失败");
            }

            InitializeTrayIcon();
            Log.Information("托盘图标已恢复");
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

            _msgWindow?.Dispose();
            _trayIcon?.Dispose();
            _guard?.Dispose();
            await Log.CloseAndFlushAsync();

            base.OnExit(e);
        }
    }
}
