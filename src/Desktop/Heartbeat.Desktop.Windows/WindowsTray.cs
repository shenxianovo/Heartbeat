using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Heartbeat.Desktop.Windows;

internal sealed class WindowsTray : IDisposable
{
    private const uint CallbackMessage = 0x802a;
    private readonly nint _window;
    private readonly Action _show, _quit;
    private readonly SubclassProcedure _procedure;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData _icon;

    public WindowsTray(nint window, Action show, Action quit)
    {
        _window = window;
        _show = show;
        _quit = quit;
        _procedure = Handle;
        if (!SetWindowSubclass(window, _procedure, 1, 0)) throw new Win32Exception();
        _icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1, Flags = 7,
            Callback = CallbackMessage, Icon = LoadIcon(0, 32512), Tip = "Heartbeat Dev", Info = string.Empty, InfoTitle = string.Empty,
        };
        if (!ShellNotifyIcon(0, ref _icon))
        {
            RemoveWindowSubclass(window, _procedure, 1);
            throw new Win32Exception();
        }
    }

    private nint Handle(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (message == _taskbarCreated) ShellNotifyIcon(0, ref _icon);
            if (message == CallbackMessage)
            {
                if (lParam == 0x0203) _show();
                else if (lParam == 0x0205) ShowMenu();
            }
        }
        catch (Exception) { Console.Error.WriteLine("Heartbeat tray operation failed."); }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "打开 Heartbeat Dev");
            AppendMenu(menu, 0, 2, "退出 Heartbeat Dev");
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            var selected = TrackPopupMenu(menu, 0x182, point.X, point.Y, 0, _window, 0);
            PostMessage(_window, 0, 0, 0);
            if (selected == 1) _show();
            else if (selected == 2) _quit();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        ShellNotifyIcon(2, ref _icon);
        RemoveWindowSubclass(_window, _procedure, 1);
    }

    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint resource);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
