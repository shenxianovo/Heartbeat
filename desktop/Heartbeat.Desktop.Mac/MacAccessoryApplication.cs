using System.Runtime.InteropServices;

namespace Heartbeat.Desktop.Mac;

internal static class MacAccessoryApplication
{
    public static void Enable()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var application = Native.objc_msgSend(
            Native.objc_getClass("NSApplication"),
            Native.sel_registerName("sharedApplication"));
        if (application != 0)
        {
            // NSApplicationActivationPolicyAccessory = 1: menu bar app, no persistent Dock icon.
            Native.objc_msgSend_setActivationPolicy(
                application,
                Native.sel_registerName("setActivationPolicy:"),
                1);
        }
    }

    private static class Native
    {
        private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjCLibrary, CharSet = CharSet.Ansi)]
        public static extern nint objc_getClass(string name);

        [DllImport(ObjCLibrary, CharSet = CharSet.Ansi)]
        public static extern nint sel_registerName(string name);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern nint objc_msgSend(nint receiver, nint selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool objc_msgSend_setActivationPolicy(
            nint receiver,
            nint selector,
            nint policy);
    }
}
