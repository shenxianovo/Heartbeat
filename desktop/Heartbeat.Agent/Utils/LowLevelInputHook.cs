using System.Runtime.InteropServices;

namespace Heartbeat.Agent.Utils
{
    /// <summary>
    /// 低级键盘/鼠标钩子（WH_KEYBOARD_LL / WH_MOUSE_LL），详见 ADR-012。
    /// 生产实现自持专用钩子线程（内部消息泵）：StartHook 立即返回，StopHook 阻塞收尾
    /// （三个 Win32 消息泵组件的统一形态）。
    /// 回调保持最小工作（解析 + 转发），避免触发 LowLevelHooksTimeout 被系统摘钩。
    /// </summary>
    public interface ILowLevelInputHook
    {
        event Action<int>? KeyDown;
        event Action<int>? KeyUp;
        event Action<short>? MouseButton;  // 1=左 2=右 3=中
        event Action<int>? Scroll;          // 原始 wheel delta
        void StartHook();
        void StopHook();
    }

    public sealed class WindowsLowLevelInputHook : ILowLevelInputHook
    {
        // ── 事件 ──
        public event Action<int>? KeyDown;
        public event Action<int>? KeyUp;
        public event Action<short>? MouseButton;
        public event Action<int>? Scroll;

        // ── Win32 常量 ──
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MOUSEWHEEL = 0x020A;

        private const uint WM_QUIT = 0x0012;

        // ── P/Invoke ──
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

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
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int pt_x;
            public int pt_y;
            public uint mouseData;   // 高位字为滚轮 delta
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // 保持委托引用，防止 GC 回收
        private HookProc? _keyboardProc;
        private HookProc? _mouseProc;
        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private uint _threadId;
        private Thread? _thread;

        public void StartHook()
        {
            _thread = new Thread(() =>
            {
                try { RunMessageLoop(); }
                catch (Exception ex) { Serilog.Log.Error(ex, "低级输入钩子线程异常"); }
            })
            {
                IsBackground = true,
                Name = "InputHookThread"
            };
            _thread.Start();
        }

        public void StopHook()
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(TimeSpan.FromSeconds(3));
        }

        /// <summary>安装钩子并阻塞运行消息循环（在自持线程上执行，低级钩子要求线程有消息泵）。</summary>
        private void RunMessageLoop()
        {
            _threadId = GetCurrentThreadId();
            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;

            var hMod = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);

            int ret;
            while ((ret = GetMessage(out MSG msg, IntPtr.Zero, 0, 0)) != 0)
            {
                if (ret == -1) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
        }

        private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 回调内吞异常：异常若穿过 P/Invoke 边界，行为未定义且可能导致系统摘钩。
            // 无论如何都要走到 CallNextHookEx，不破坏钩子链。
            if (nCode >= 0)
            {
                try
                {
                    int msg = (int)wParam;
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)data.vkCode;

                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                        KeyDown?.Invoke(vk);
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                        KeyUp?.Invoke(vk);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "键盘钩子回调异常");
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int msg = (int)wParam;
                    switch (msg)
                    {
                        case WM_LBUTTONDOWN:
                            MouseButton?.Invoke(1);
                            break;
                        case WM_RBUTTONDOWN:
                            MouseButton?.Invoke(2);
                            break;
                        case WM_MBUTTONDOWN:
                            MouseButton?.Invoke(3);
                            break;
                        case WM_MOUSEWHEEL:
                            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                            // 高 16 位为有符号 delta
                            short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                            Scroll?.Invoke(delta);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "鼠标钩子回调异常");
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
    }
}
