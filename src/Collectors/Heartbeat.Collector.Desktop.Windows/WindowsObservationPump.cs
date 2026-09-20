using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Heartbeat.Collector.Desktop.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsObservationPump : IDisposable
{
    private readonly Action<DesktopObservation> _publish;
    private readonly Action _foregroundChanged;
    private readonly Action _failed;
    private readonly WindowsNative.WindowProcedure _procedure;
    private readonly WindowsNative.WinEventProcedure _winEvent;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _className = "Heartbeat.Observation." + Guid.NewGuid().ToString("N");
    private nint _window, _foregroundHook, _titleHook, _power;
    private bool _registered, _rawInput;
    private static readonly Guid DisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    public WindowsObservationPump(Action<DesktopObservation> publish, Action foregroundChanged, Action failed)
    {
        _publish = publish;
        _foregroundChanged = foregroundChanged;
        _failed = failed;
        _procedure = Dispatch;
        _winEvent = OnWinEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "Heartbeat Windows observations" };
    }

    public void Start()
    {
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        try
        {
            CreateWindow();
            RegisterObservations();
            _ready.TrySetResult();
            int result;
            while ((result = WindowsNative.GetMessage(out var message, 0, 0, 0)) > 0)
            {
                WindowsNative.TranslateMessage(ref message);
                WindowsNative.DispatchMessage(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            _failed();
        }
        finally { Cleanup(); }
    }

    private void CreateWindow()
    {
        var windowClass = new WindowsNative.WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowsNative.WindowClass>(), Procedure = _procedure,
            Instance = WindowsNative.GetModuleHandle(null), ClassName = _className,
        };
        if (WindowsNative.RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        _window = WindowsNative.CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0, 0, 0, windowClass.Instance, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private void RegisterObservations()
    {
        _foregroundHook = WindowsNative.SetWinEventHook(3, 3, 0, _winEvent, 0, 0, 0);
        _titleHook = WindowsNative.SetWinEventHook(0x800c, 0x800c, 0, _winEvent, 0, 0, 0);
        if (_foregroundHook == 0 || _titleHook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        _registered = WindowsNative.WTSRegisterSessionNotification(_window, 0);
        if (!_registered) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var display = DisplayState;
        _power = WindowsNative.RegisterPowerSettingNotification(_window, ref display, 0);
        if (_power == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        WindowsNative.RawDevice[] devices = [
            new() { Page = 1, Usage = 6, Flags = 0x100, Window = _window },
            new() { Page = 1, Usage = 2, Flags = 0x100, Window = _window }];
        _rawInput = WindowsNative.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<WindowsNative.RawDevice>());
        _publish(new DesktopObservation.Capability(new CapabilityObservation(ObservationCapability.Input,
            _rawInput ? ObservationState.Available : ObservationState.Unavailable,
            _rawInput ? null : "raw_input_registration_failed")));
    }

    private void OnWinEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (eventId == 3 || (objectId == 0 && window == WindowsNative.GetForegroundWindow())) _foregroundChanged();
        }
        catch (Exception) { _failed(); }
    }

    private nint Dispatch(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case 0x00ff: ReadInput(lParam); break;
                case 0x02b1: SessionChanged(wParam); break;
                case 0x0218: PowerChanged(wParam, lParam); break;
                case 0x0010: WindowsNative.PostQuitMessage(0); return 0;
            }
        }
        catch (Exception) { _failed(); }
        return WindowsNative.DefWindowProc(window, message, wParam, lParam);
    }

    private void ReadInput(nint input)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<WindowsNative.RawHeader>();
        if (WindowsNative.GetRawInputData(input, 0x10000003, 0, ref size, headerSize) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (size < headerSize || size > 4096) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (WindowsNative.GetRawInputData(input, 0x10000003, buffer, ref size, headerSize) != size)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            PublishInput(buffer, size, headerSize);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void PublishInput(nint buffer, uint size, uint headerSize)
    {
        var header = Marshal.PtrToStructure<WindowsNative.RawHeader>(buffer);
        var data = buffer + (int)headerSize;
        if (header.Type == 1 && size >= headerSize + Marshal.SizeOf<WindowsNative.RawKeyboard>())
        {
            var key = Marshal.PtrToStructure<WindowsNative.RawKeyboard>(data);
            if (WindowsInputTranslator.Keyboard(key.ScanCode, key.Flags) is { } observation)
                _publish(new DesktopObservation.Input(observation));
        }
        else if (header.Type == 0 && size >= headerSize + Marshal.SizeOf<WindowsNative.RawMouse>())
        {
            var mouse = Marshal.PtrToStructure<WindowsNative.RawMouse>(data);
            foreach (var observation in WindowsInputTranslator.Mouse(mouse.Buttons, mouse.Data))
                _publish(new DesktopObservation.Input(observation));
        }
    }

    private void SessionChanged(nuint state)
    {
        switch (state)
        {
            case 7: Away(DesktopAwayReason.ScreenLocked, true); break;
            case 8: Away(DesktopAwayReason.ScreenLocked, false); break;
            case 2 or 4 or 6: Away(DesktopAwayReason.SessionInactive, true); break;
            case 1 or 3 or 5: Away(DesktopAwayReason.SessionInactive, false); break;
        }
    }

    private void PowerChanged(nuint state, nint data)
    {
        if (state == 4) Away(DesktopAwayReason.SystemSleep, true);
        else if (state is 7 or 18) Away(DesktopAwayReason.SystemSleep, false);
        else if (state == 0x8013 && data != 0 && Marshal.PtrToStructure<Guid>(data) == DisplayState && Marshal.ReadInt32(data, 16) == 4)
            Away(DesktopAwayReason.DisplaySleep, Marshal.ReadInt32(data, 20) == 0);
    }

    private void Away(DesktopAwayReason reason, bool entered)
    {
        _publish(entered ? new DesktopObservation.AwayEntered(reason) : new DesktopObservation.AwayExited(reason, null));
        if (!entered) _foregroundChanged();
    }

    private void Cleanup()
    {
        if (_rawInput)
        {
            WindowsNative.RawDevice[] devices = [new() { Page = 1, Usage = 6, Flags = 1 }, new() { Page = 1, Usage = 2, Flags = 1 }];
            WindowsNative.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<WindowsNative.RawDevice>());
        }
        if (_registered) WindowsNative.WTSUnRegisterSessionNotification(_window);
        if (_power != 0) WindowsNative.UnregisterPowerSettingNotification(_power);
        if (_foregroundHook != 0) WindowsNative.UnhookWinEvent(_foregroundHook);
        if (_titleHook != 0) WindowsNative.UnhookWinEvent(_titleHook);
        if (_window != 0) WindowsNative.DestroyWindow(_window);
        WindowsNative.UnregisterClass(_className, WindowsNative.GetModuleHandle(null));
    }

    public void Dispose()
    {
        if (!_thread.IsAlive) return;
        WindowsNative.PostMessage(_window, 0x0010, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new IOException("Windows 采集线程未能及时停止。");
    }
}
