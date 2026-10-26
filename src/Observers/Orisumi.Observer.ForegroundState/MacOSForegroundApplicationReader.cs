using System.Runtime.InteropServices;

namespace Orisumi.Observers.ForegroundState;

internal static partial class MacOSForegroundApplicationReader
{
    private static readonly Lazy<nint> AppKit = new(() =>
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"));
    private static readonly Lazy<nint> DefaultRunLoopMode = new(() => Marshal.ReadIntPtr(
        NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopDefaultMode")));
    private static readonly Lazy<double> TickNanoseconds = new(() =>
    {
        if (GetTimebase(out var timebase) != 0 || timebase.Denominator == 0)
            throw new InvalidOperationException("macOS clock timebase is unavailable.");
        return (double)timebase.Numerator / timebase.Denominator;
    });

    internal static ForegroundApplicationReading Read() => ReadApplication(0);

    internal static ForegroundApplicationReading ReadApplication(nint application)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Foreground application collection requires macOS.");
        _ = AppKit.Value;
        var pool = Send(Send(GetClass("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            if (application == 0)
            {
                var workspace = Send(GetClass("NSWorkspace"), "sharedWorkspace");
                if (workspace == 0) throw new InvalidOperationException("NSWorkspace is unavailable.");
                application = Send(workspace, "frontmostApplication");
            }
            var observedAt = DateTimeOffset.UtcNow;
            if (application == 0)
                throw new InvalidOperationException("macOS did not return a foreground application.");
            var launchDate = Send(application, "launchDate");
            var processId = (int)Send(application, "processIdentifier");
            var elapsed = ToTimeSpan(ContinuousTime());
            var awake = ToTimeSpan(AbsoluteTime());
            return new ForegroundApplicationReading(
                ReadString(Send(application, "bundleIdentifier")),
                ReadString(Send(application, "localizedName")),
                ReadString(Send(Send(application, "executableURL"), "path")),
                observedAt,
                TimeZoneInfo.Local.Id,
                processId > 0 ? processId : null,
                launchDate == 0 ? null : DateTimeOffset.UnixEpoch.AddSeconds(
                    SendDouble(launchDate, RegisterSelector("timeIntervalSince1970"))),
                elapsed, awake);
        }
        finally
        {
            Send(pool, "drain");
        }
    }

    internal static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(AppKit.Value, name));

    internal static void PumpEvents()
    {
        var result = RunLoop(DefaultRunLoopMode.Value, 0.05, 1);
        if (result == 1) Thread.Sleep(10); // A run loop without sources returns immediately.
    }

    internal static nint Send(nint receiver, string selector) =>
        SendMessage(receiver, RegisterSelector(selector));

    internal static string? ReadString(nint value) => value == 0
        ? null
        : Marshal.PtrToStringUTF8(Send(value, "UTF8String"));

    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopRunInMode")]
    private static partial int RunLoop(nint mode, double seconds, byte returnAfterSourceHandled);

    private static TimeSpan ToTimeSpan(ulong ticks) => TimeSpan.FromTicks((long)(ticks * TickNanoseconds.Value / 100));

    [StructLayout(LayoutKind.Sequential)]
    private struct Timebase
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [LibraryImport(SystemLibrary, EntryPoint = "mach_timebase_info")]
    private static partial int GetTimebase(out Timebase timebase);

    [LibraryImport(SystemLibrary, EntryPoint = "mach_continuous_time")]
    private static partial ulong ContinuousTime();

    [LibraryImport(SystemLibrary, EntryPoint = "mach_absolute_time")]
    private static partial ulong AbsoluteTime();

    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial double SendDouble(nint receiver, nint selector);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetClass(string name);

    [LibraryImport(ObjectiveC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint RegisterSelector(string name);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial nint SendMessage(nint receiver, nint selector);
}
