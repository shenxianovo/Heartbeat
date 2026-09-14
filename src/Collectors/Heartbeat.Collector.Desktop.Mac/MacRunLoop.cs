using System.Runtime.InteropServices;

namespace Heartbeat.Collector.Desktop.Mac;

internal static class MacRunLoop
{
    public static int Run(Task<int> operation)
    {
        if (!operation.IsCompleted)
        {
            var framework = NativeLibrary.Load(CoreFoundation);
            try
            {
                var mode = Marshal.ReadIntPtr(NativeLibrary.GetExport(framework, "kCFRunLoopDefaultMode"));
                while (!operation.IsCompleted)
                {
                    // AppKit refreshes NSWorkspace state through the main run loop.
                    // Keep servicing it while sampling and HTTP delivery await independently.
                    var result = CFRunLoopRunInMode(mode, 0.1, false);
                    if (result == 1) // No sources registered: avoid a busy loop.
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            finally
            {
                NativeLibrary.Free(framework);
            }
        }

        return operation.GetAwaiter().GetResult();
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundation)]
    private static extern int CFRunLoopRunInMode(nint mode, double seconds,
        [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
}
