using System.Runtime.InteropServices;

namespace Heartbeat.Collector.Desktop.Mac;

public sealed class MacForegroundApplicationReader : IForegroundApplicationReader
{
    private readonly nint _workspace;

    public MacForegroundApplicationReader()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The desktop macOS Collector requires macOS.");
        }

        _ = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        _workspace = ObjC.Send(ObjC.Class("NSWorkspace"), "sharedWorkspace");
        if (_workspace == 0)
        {
            throw new InvalidOperationException("Unable to access NSWorkspace.");
        }
    }

    public ForegroundApplication? Read()
    {
        var application = ObjC.Send(_workspace, "frontmostApplication");
        if (application == 0)
        {
            return null;
        }

        var bundleIdentifier = ObjC.ReadString(ObjC.Send(application, "bundleIdentifier"));
        if (!string.IsNullOrWhiteSpace(bundleIdentifier))
        {
            return new ForegroundApplication("macos", "bundle_id", bundleIdentifier.Trim());
        }

        var executableUrl = ObjC.Send(application, "executableURL");
        var executablePath = executableUrl == 0
            ? null
            : ObjC.ReadString(ObjC.Send(executableUrl, "path"));
        return string.IsNullOrWhiteSpace(executablePath)
            ? null
            : new ForegroundApplication("macos", "executable_path", executablePath.Trim());
    }

    private static class ObjC
    {
        public static nint Class(string name) => Native.objc_getClass(name);

        public static nint Selector(string name) => Native.sel_registerName(name);

        public static nint Send(nint receiver, string selector) =>
            Native.objc_msgSend(receiver, Selector(selector));

        public static string? ReadString(nint value)
        {
            if (value == 0)
            {
                return null;
            }

            var utf8 = Native.objc_msgSend(value, Selector("UTF8String"));
            return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
        }
    }

    private static class Native
    {
        private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjCLibrary, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        public static extern nint objc_getClass(string name);

        [DllImport(ObjCLibrary, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        public static extern nint sel_registerName(string name);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern nint objc_msgSend(nint receiver, nint selector);
    }
}
