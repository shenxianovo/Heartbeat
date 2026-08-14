using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Heartbeat.Desktop.Mac.Observations;

namespace Heartbeat.Desktop.Mac.Native;

/// <summary>
/// NSWorkspace 的窄 adapter。仅订阅无需 TCC 权限的 workspace 通知，并读取
/// frontmostApplication 的 bundle/executable/display name；不触碰 Accessibility。
/// </summary>
public sealed class CocoaWorkspaceNative : IMacWorkspaceNative, IDisposable
{
    private static readonly nint AppKitHandle = LoadAppKit();
    private static readonly ConcurrentDictionary<nint, WeakReference<CocoaWorkspaceNative>> Instances = [];
    private static readonly NotificationCallback Callback = HandleNotification;
    private static readonly nint ObserverClass = CreateObserverClass();

    private readonly object _gate = new();
    private readonly nint _workspace;
    private readonly nint _notificationCenter;
    private readonly nint _distributedNotificationCenter;
    private readonly nint _observer;
    private bool _started;
    private bool _disposed;

    public CocoaWorkspaceNative()
    {
        EnsureMacOS();
        // Keep the framework load in the constructor as well: the host starts before Avalonia
        // creates NSApplication, so AppKit cannot be assumed to have been loaded by the UI backend.
        var appKit = LoadAppKit();
        var workspaceClass = ObjC.Class("NSWorkspace");
        _workspace = ObjC.Send(workspaceClass, "sharedWorkspace");
        _notificationCenter = ObjC.Send(_workspace, "notificationCenter");
        _distributedNotificationCenter = ObjC.Send(
            ObjC.Class("NSDistributedNotificationCenter"),
            "defaultCenter");
        _observer = ObjC.Send(ObjC.Send(ObserverClass, "alloc"), "init");
        if (_workspace == 0 || _notificationCenter == 0 ||
            _distributedNotificationCenter == 0 || _observer == 0)
            throw new InvalidOperationException(
                $"Unable to initialize NSWorkspace notifications " +
                $"(appkit=0x{appKit:x}, class=0x{workspaceClass:x}, workspace=0x{_workspace:x}, " +
                $"center=0x{_notificationCenter:x}, distributed=0x{_distributedNotificationCenter:x}, " +
                $"observer=0x{_observer:x}).");
        GC.KeepAlive(appKit);
        Instances[_observer] = new WeakReference<CocoaWorkspaceNative>(this);
    }

    public event Action<string>? Notification;

    public MacApplication? FrontmostApplication
    {
        get
        {
            var application = ObjC.Send(_workspace, "frontmostApplication");
            if (application == 0)
                return null;

            var bundleIdentifier = ObjC.ReadString(ObjC.Send(application, "bundleIdentifier"));
            var displayName = ObjC.ReadString(ObjC.Send(application, "localizedName"));
            var executableUrl = ObjC.Send(application, "executableURL");
            var executablePath = executableUrl == 0
                ? null
                : ObjC.ReadString(ObjC.Send(executableUrl, "path"));
            return new MacApplication(bundleIdentifier, executablePath, displayName);
        }
    }

    public void Start(IReadOnlyCollection<string> notificationNames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_started) return;
            foreach (var name in notificationNames)
            {
                var center = IsScreenLockNotification(name)
                    ? _distributedNotificationCenter
                    : _notificationCenter;
                ObjC.SendVoid(
                    center,
                    "addObserver:selector:name:object:",
                    _observer,
                    ObjC.Selector("heartbeatNotification:"),
                    ObjC.String(name),
                    0);
            }
            _started = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            ObjC.SendVoid(_notificationCenter, "removeObserver:", _observer);
            ObjC.SendVoid(_distributedNotificationCenter, "removeObserver:", _observer);
            _started = false;
        }
    }

    private static bool IsScreenLockNotification(string name) =>
        name is MacWorkspaceNotification.ScreenLocked or MacWorkspaceNotification.ScreenUnlocked;

    private static void HandleNotification(nint self, nint _, nint notification)
    {
        if (!Instances.TryGetValue(self, out var weak) || !weak.TryGetTarget(out var instance))
            return;

        var name = ObjC.ReadString(ObjC.Send(notification, "name"));
        if (name != null)
            instance.Notification?.Invoke(name);
    }

    private static nint CreateObserverClass()
    {
        EnsureMacOS();
        const string className = "HeartbeatWorkspaceObserver";
        var existing = Native.objc_getClass(className);
        if (existing != 0)
            return existing;

        var observerClass = Native.objc_allocateClassPair(Native.objc_getClass("NSObject"), className, 0);
        if (observerClass == 0)
            throw new InvalidOperationException("Unable to allocate Objective-C workspace observer.");

        var implementation = Marshal.GetFunctionPointerForDelegate(Callback);
        if (!Native.class_addMethod(
                observerClass,
                ObjC.Selector("heartbeatNotification:"),
                implementation,
                "v@:@"))
            throw new InvalidOperationException("Unable to register workspace notification callback.");

        Native.objc_registerClassPair(observerClass);
        return observerClass;
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Cocoa workspace events require macOS.");
    }

    private static nint LoadAppKit()
    {
        EnsureMacOS();
        return NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        Instances.TryRemove(_observer, out _);
        ObjC.SendVoid(_observer, "release");
        GC.KeepAlive(AppKitHandle);
        GC.SuppressFinalize(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationCallback(nint self, nint selector, nint notification);

    private static class ObjC
    {
        public static nint Class(string name) => Native.objc_getClass(name);
        public static nint Selector(string name) => Native.sel_registerName(name);
        public static nint Send(nint receiver, string selector) =>
            Native.objc_msgSend(receiver, Selector(selector));
        public static void SendVoid(nint receiver, string selector) =>
            Native.objc_msgSend_void(receiver, Selector(selector));
        public static void SendVoid(nint receiver, string selector, nint argument) =>
            Native.objc_msgSend_void_1(receiver, Selector(selector), argument);
        public static void SendVoid(
            nint receiver,
            string selector,
            nint first,
            nint second,
            nint third,
            nint fourth) =>
            Native.objc_msgSend_void_4(receiver, Selector(selector), first, second, third, fourth);

        public static nint String(string value) =>
            Native.objc_msgSend_string(Class("NSString"), Selector("stringWithUTF8String:"), value);

        public static string? ReadString(nint value)
        {
            if (value == 0) return null;
            var utf8 = Native.objc_msgSend(value, Selector("UTF8String"));
            return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
        }
    }

    private static class Native
    {
        private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjCLibrary, CharSet = CharSet.Ansi)]
        public static extern nint objc_getClass(string name);

        [DllImport(ObjCLibrary, CharSet = CharSet.Ansi)]
        public static extern nint sel_registerName(string name);

        [DllImport(ObjCLibrary, CharSet = CharSet.Ansi)]
        public static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

        [DllImport(ObjCLibrary, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool class_addMethod(nint cls, nint name, nint implementation, string types);

        [DllImport(ObjCLibrary)]
        public static extern void objc_registerClassPair(nint cls);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern nint objc_msgSend(nint receiver, nint selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern void objc_msgSend_void(nint receiver, nint selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern void objc_msgSend_void_1(nint receiver, nint selector, nint argument);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern void objc_msgSend_void_4(
            nint receiver,
            nint selector,
            nint first,
            nint second,
            nint third,
            nint fourth);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend", CharSet = CharSet.Ansi)]
        public static extern nint objc_msgSend_string(nint receiver, nint selector, string value);
    }
}
