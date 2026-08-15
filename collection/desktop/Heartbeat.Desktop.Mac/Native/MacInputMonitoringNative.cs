using System.Runtime.InteropServices;
using Heartbeat.Desktop.Mac.Input;
using Serilog;

namespace Heartbeat.Desktop.Mac.Native;

/// <summary>
/// Passive CoreGraphics event-tap adapter. It never requests permission from StartListening;
/// permission prompts are reserved for explicit user actions through RequestAuthorization.
/// </summary>
public sealed class MacInputMonitoringNative : IMacInputMonitoringNative, IDisposable
{
    private const uint SessionEventTap = 1;
    private const uint HeadInsertEventTap = 0;
    private const uint ListenOnly = 1;
    private const uint TapDisabledByTimeout = 0xFFFFFFFE;
    private const uint TapDisabledByUserInput = 0xFFFFFFFF;
    private const uint KeyboardEventKeycode = 9;
    private const uint MouseEventButtonNumber = 3;
    private const uint ScrollWheelEventDeltaAxis1 = 11;
    private const uint ScrollWheelEventIsContinuous = 88;
    private const uint ScrollWheelEventPointDeltaAxis1 = 96;
    private static readonly uint[] ObservedEventTypes = [1, 3, 10, 11, 22, 25];
    private static readonly ulong EventMask = ObservedEventTypes.Aggregate(
        0UL,
        (mask, type) => mask | (1UL << (int)type));
    private static readonly nint DefaultRunLoopMode = Native.CFStringCreateWithCString(
        0,
        "kCFRunLoopDefaultMode",
        0x08000100);
    private static readonly EventTapCallback Callback = HandleEvent;

    private readonly object _gate = new();
    private CancellationTokenSource? _threadCts;
    private Thread? _thread;
    private nint _eventTap;
    private nint _runLoop;
    private bool _disposed;

    public MacInputMonitoringNative()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS Input Monitoring requires macOS.");
    }

    public event Action<MacInputObservation>? Observation;

    public bool IsAvailable => OperatingSystem.IsMacOSVersionAtLeast(10, 15);
    public bool IsAuthorized => IsAvailable && Native.CGPreflightListenEventAccess();

    public void RequestAuthorization()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsAvailable)
            _ = Native.CGRequestListenEventAccess();
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAuthorized) return;

        lock (_gate)
        {
            if (_thread is { IsAlive: true }) return;
            _threadCts = new CancellationTokenSource();
            _thread = new Thread(() => RunEventLoop(_threadCts.Token))
            {
                IsBackground = true,
                Name = "Heartbeat macOS Input Monitoring"
            };
            _thread.Start();
        }
    }

    public void StopListening()
    {
        Thread? thread;
        lock (_gate)
        {
            _threadCts?.Cancel();
            thread = _thread;
            if (_runLoop != 0)
                Native.CFRunLoopStop(_runLoop);
        }

        if (thread != null && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(2));

        lock (_gate)
        {
            _thread = null;
            _threadCts?.Dispose();
            _threadCts = null;
        }
    }

    private void RunEventLoop(CancellationToken cancellationToken)
    {
        var handle = GCHandle.Alloc(this);
        var source = nint.Zero;
        try
        {
            _eventTap = Native.CGEventTapCreate(
                SessionEventTap,
                HeadInsertEventTap,
                ListenOnly,
                EventMask,
                Callback,
                GCHandle.ToIntPtr(handle));
            if (_eventTap == 0)
            {
                Log.Warning("macOS Input Monitoring event tap 创建失败");
                return;
            }

            source = Native.CFMachPortCreateRunLoopSource(0, _eventTap, 0);
            if (source == 0) return;
            _runLoop = Native.CFRunLoopGetCurrent();
            Native.CFRunLoopAddSource(_runLoop, source, DefaultRunLoopMode);
            Native.CGEventTapEnable(_eventTap, true);

            while (!cancellationToken.IsCancellationRequested)
                _ = Native.CFRunLoopRunInMode(DefaultRunLoopMode, 0.2, true);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "macOS Input Monitoring event tap 异常停止");
        }
        finally
        {
            if (_runLoop != 0 && source != 0)
                Native.CFRunLoopRemoveSource(_runLoop, source, DefaultRunLoopMode);
            if (source != 0) Native.CFRelease(source);
            if (_eventTap != 0) Native.CFRelease(_eventTap);
            _eventTap = 0;
            _runLoop = 0;
            if (handle.IsAllocated) handle.Free();
        }
    }

    private static nint HandleEvent(
        nint proxy,
        uint eventType,
        nint eventReference,
        nint userInfo)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(userInfo);
            if (handle.Target is not MacInputMonitoringNative instance)
                return eventReference;

            if (eventType is TapDisabledByTimeout or TapDisabledByUserInput)
            {
                if (instance._eventTap != 0)
                    Native.CGEventTapEnable(instance._eventTap, true);
                return eventReference;
            }

            var keyCode = Native.CGEventGetIntegerValueField(eventReference, KeyboardEventKeycode);
            var mouseButton = Native.CGEventGetIntegerValueField(eventReference, MouseEventButtonNumber);
            var lineScroll = Native.CGEventGetIntegerValueField(
                eventReference,
                ScrollWheelEventDeltaAxis1);
            var continuous = Native.CGEventGetIntegerValueField(
                eventReference,
                ScrollWheelEventIsContinuous) != 0;
            var pointScroll = Native.CGEventGetIntegerValueField(
                eventReference,
                ScrollWheelEventPointDeltaAxis1);
            var scrollDelta = MacInputNativeEventTranslator.NormalizeScrollDelta(
                continuous,
                lineScroll,
                pointScroll);
            if (MacInputNativeEventTranslator.TryTranslate(
                eventType,
                keyCode,
                mouseButton,
                scrollDelta,
                out var observation))
            {
                instance.Observation?.Invoke(observation);
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "macOS Input Monitoring 回调异常");
        }
        return eventReference;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopListening();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint EventTapCallback(
        nint proxy,
        uint eventType,
        nint eventReference,
        nint userInfo);

    private static class Native
    {
        private const string ApplicationServices =
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(ApplicationServices)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CGPreflightListenEventAccess();

        [DllImport(ApplicationServices)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CGRequestListenEventAccess();

        [DllImport(ApplicationServices)]
        public static extern nint CGEventTapCreate(
            uint tap,
            uint place,
            uint options,
            ulong eventsOfInterest,
            EventTapCallback callback,
            nint userInfo);

        [DllImport(ApplicationServices)]
        public static extern void CGEventTapEnable(
            nint tap,
            [MarshalAs(UnmanagedType.I1)] bool enable);

        [DllImport(ApplicationServices)]
        public static extern long CGEventGetIntegerValueField(nint eventReference, uint field);

        [DllImport(CoreFoundation)]
        public static extern nint CFMachPortCreateRunLoopSource(
            nint allocator,
            nint port,
            nint order);

        [DllImport(CoreFoundation)]
        public static extern nint CFStringCreateWithCString(
            nint allocator,
            string value,
            uint encoding);

        [DllImport(CoreFoundation)]
        public static extern nint CFRunLoopGetCurrent();

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopAddSource(nint runLoop, nint source, nint mode);

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopRemoveSource(nint runLoop, nint source, nint mode);

        [DllImport(CoreFoundation)]
        public static extern int CFRunLoopRunInMode(
            nint mode,
            double seconds,
            [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopStop(nint runLoop);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(nint value);
    }
}
