using System.Runtime.InteropServices;

namespace Heartbeat.Desktop.Windows.Utils
{
    /// <summary>
    /// 基于 message-only 窗口接收 WM_POWERBROADCAST 的电源监视器。详见 ADR-014。
    /// 在专用线程上注册窗口类、创建 HWND_MESSAGE 窗口、注册显示状态通知，
    /// 并运行 GetMessage 消息泵——与 WindowsLowLevelInputHook 同模式。
    /// </summary>
    public sealed class WindowsPowerMonitor : IPowerMonitor
    {
        public event Action? DisplayOff;
        public event Action? DisplayOn;
        public event Action? Suspend;
        public event Action? Resume;

        private Thread? _thread;

        // ── Win32 常量 ──
        private const int WM_POWERBROADCAST = 0x0218;
        private const int WM_DESTROY = 0x0002;
        private const uint WM_QUIT = 0x0012;

        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;

        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x0;

        // GUID_CONSOLE_DISPLAY_STATE {6FE69556-704A-47A0-8F24-C28D936FDA47}
        // Data: 0=off, 1=on, 2=dimmed
        private static readonly Guid GUID_CONSOLE_DISPLAY_STATE =
            new("6FE69556-704A-47A0-8F24-C28D936FDA47");

        private static readonly IntPtr HWND_MESSAGE = new(-3);

        // ── P/Invoke ──
        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data; // 第一个字节即显示状态
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(
            IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        // 保持委托与状态引用，防止 GC 回收
        private WndProc? _wndProc;
        private IntPtr _hwnd;
        private IntPtr _powerNotify;
        private uint _threadId;
        private string _className = "";

        public void Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "PowerMonitorThread"
            };
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                _wndProc = WindowProc;
                _className = "HeartbeatPowerMonitor_" + Guid.NewGuid().ToString("N");
                var hInstance = GetModuleHandle(null);

                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = _wndProc,
                    hInstance = hInstance,
                    lpszClassName = _className,
                };

                if (RegisterClassEx(ref wc) == 0)
                {
                    Serilog.Log.Error("注册电源监视窗口类失败: {Err}", Marshal.GetLastWin32Error());
                    return;
                }

                _hwnd = CreateWindowEx(0, _className, "HeartbeatPowerMonitor", 0,
                    0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    Serilog.Log.Error("创建电源监视窗口失败: {Err}", Marshal.GetLastWin32Error());
                    UnregisterClass(_className, hInstance);
                    return;
                }

                var guid = GUID_CONSOLE_DISPLAY_STATE;
                _powerNotify = RegisterPowerSettingNotification(_hwnd, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_powerNotify == IntPtr.Zero)
                    Serilog.Log.Warning("注册显示状态通知失败: {Err}", Marshal.GetLastWin32Error());

                // 消息循环
                int ret;
                while ((ret = GetMessage(out MSG msg, IntPtr.Zero, 0, 0)) != 0)
                {
                    if (ret == -1) break;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                Cleanup(hInstance);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "电源监视线程异常");
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_POWERBROADCAST)
            {
                int evt = (int)wParam;
                switch (evt)
                {
                    case PBT_APMSUSPEND:
                        Suspend?.Invoke();
                        break;
                    case PBT_APMRESUMESUSPEND:
                    case PBT_APMRESUMEAUTOMATIC:
                        Resume?.Invoke();
                        break;
                    case PBT_POWERSETTINGCHANGE:
                        var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                        if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                        {
                            // 0=off, 1=on, 2=dimmed（dimmed 视为仍在用，忽略）
                            if (setting.Data == 0) DisplayOff?.Invoke();
                            else if (setting.Data == 1) DisplayOn?.Invoke();
                        }
                        break;
                }
                return IntPtr.Zero;
            }

            if (msg == WM_DESTROY)
                return IntPtr.Zero;

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void Cleanup(IntPtr hInstance)
        {
            if (_powerNotify != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_powerNotify);
                _powerNotify = IntPtr.Zero;
            }
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (!string.IsNullOrEmpty(_className))
            {
                UnregisterClass(_className, hInstance);
                _className = "";
            }
        }

        public void Stop()
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(TimeSpan.FromSeconds(3));
            _threadId = 0;
        }
    }
}
