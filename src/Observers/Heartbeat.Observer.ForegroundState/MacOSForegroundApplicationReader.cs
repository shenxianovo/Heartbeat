using System.Runtime.InteropServices;

namespace Heartbeat.Observers.ForegroundState;

internal static partial class MacOSForegroundApplicationReader
{
    private static readonly Lazy<nint> AppKit = new(() =>
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"));

    internal static ForegroundApplicationReading Read()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Foreground application collection requires macOS.");
        _ = AppKit.Value;
        var pool = Send(Send(GetClass("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            var workspace = Send(GetClass("NSWorkspace"), "sharedWorkspace");
            if (workspace == 0) throw new InvalidOperationException("NSWorkspace is unavailable.");
            var application = Send(workspace, "frontmostApplication");
            var observedAt = DateTimeOffset.UtcNow;
            if (application == 0)
                throw new InvalidOperationException("macOS did not return a foreground application.");
            return new ForegroundApplicationReading(
                ReadString(Send(application, "bundleIdentifier")),
                ReadString(Send(application, "localizedName")),
                ReadString(Send(Send(application, "executableURL"), "path")),
                observedAt,
                TimeZoneInfo.Local.Id);
        }
        finally
        {
            Send(pool, "drain");
        }
    }

    private static nint Send(nint receiver, string selector) =>
        SendMessage(receiver, RegisterSelector(selector));

    private static string? ReadString(nint value) => value == 0
        ? null
        : Marshal.PtrToStringUTF8(Send(value, "UTF8String"));

    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(ObjectiveC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjectiveC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint RegisterSelector(string name);

    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static partial nint SendMessage(nint receiver, nint selector);
}
