using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static Orisumi.Observers.ForegroundState.MacOSForegroundApplicationReader;

namespace Orisumi.Observers.ForegroundState;

internal sealed partial class MacOSForegroundEvents : IDisposable
{
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private static readonly ConcurrentDictionary<nint, MacOSForegroundEvents> Instances = new();
    private static readonly NotificationCallback Callback = Receive;
    private static readonly Lazy<nint> ObserverClass = new(CreateClass);
    private readonly ForegroundCollector _collector;
    private readonly Action<Exception> _failed;
    private readonly nint _receiver;
    private readonly nint _center;

    internal MacOSForegroundEvents(ForegroundCollector collector, Action<Exception> failed)
    {
        _collector = collector;
        _failed = failed;
        // Load AppKit before looking up its classes and exported notification names.
        var activated = Constant("NSWorkspaceDidActivateApplicationNotification");
        _center = Send(Send(GetClass("NSWorkspace"), "sharedWorkspace"), "notificationCenter");
        _receiver = Send(Send(ObserverClass.Value, "alloc"), "init");
        Instances[_receiver] = this;
        foreach (var name in new[] { activated, Constant("NSWorkspaceWillSleepNotification"), Constant("NSWorkspaceDidWakeNotification") })
            AddObserver(_center, RegisterSelector("addObserver:selector:name:object:"), _receiver,
                RegisterSelector("orisumiNotification:"), name, 0);
    }

    public void Dispose()
    {
        SendArgument(_center, RegisterSelector("removeObserver:"), _receiver);
        if (Instances.TryRemove(_receiver, out _)) Send(_receiver, "release");
    }

    private static void Receive(nint receiver, nint selector, nint notification)
    {
        if (!Instances.TryGetValue(receiver, out var instance)) return;
        try
        {
            switch (ReadString(Send(notification, "name")))
            {
                case "NSWorkspaceDidActivateApplicationNotification":
                    var info = Send(notification, "userInfo");
                    var application = SendArgument(info, RegisterSelector("objectForKey:"), Constant("NSWorkspaceApplicationKey"));
                    if (application == 0) instance._collector.Poll();
                    else instance._collector.Observe(ReadApplication(application));
                    break;
                case "NSWorkspaceWillSleepNotification": instance._collector.Sleep(); break;
                case "NSWorkspaceDidWakeNotification": instance._collector.Wake(); break;
            }
        }
        // Managed exceptions must never cross the native notification boundary.
        catch (Exception exception) { instance._failed(exception); }
    }

    private static nint CreateClass()
    {
        var type = AllocateClass(GetClass("NSObject"), "OrisumiForegroundNotifications", 0);
        if (type == 0 || AddMethod(type, RegisterSelector("orisumiNotification:"),
                Marshal.GetFunctionPointerForDelegate(Callback), "v@:@") == 0)
            throw new InvalidOperationException("Could not register macOS foreground notifications.");
        RegisterClass(type);
        return type;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationCallback(nint receiver, nint selector, nint notification);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint AllocateClass(nint superclass, string name, nuint extraBytes);
    [LibraryImport(ObjectiveC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_registerClassPair")]
    private static partial void RegisterClass(nint type);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial nint SendArgument(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial void AddObserver(nint receiver, nint selector, nint observer, nint callback, nint name, nint sender);
}
