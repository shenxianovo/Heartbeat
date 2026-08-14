using System.Runtime.InteropServices;

namespace Heartbeat.Desktop.Windows;

internal static class WindowsMessageBox
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconInformation = 0x00000040;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);

    public static void ShowAlreadyRunning() =>
        MessageBox(0, "Heartbeat 已在运行中。", "Heartbeat", MbOk | MbIconInformation);
}
