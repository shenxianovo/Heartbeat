using System.Runtime.InteropServices;
using Heartbeat.Collector.Desktop.Mac;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class MacRunLoopTests
{
    [Fact]
    public void NativeEventsProgressWhileManagedWorkIsPending()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var framework = NativeLibrary.Load(Native.CoreFoundation);
        var mode = Marshal.ReadIntPtr(NativeLibrary.GetExport(framework, "kCFRunLoopDefaultMode"));
        Native.TimerCallback callback = (_, _) => completion.TrySetResult(42);
        var timer = Native.CFRunLoopTimerCreate(0, Native.CFAbsoluteTimeGetCurrent() + 0.05, 0, 0, 0, callback, 0);
        using var timeout = new Timer(_ => completion.TrySetException(new TimeoutException("Native run loop was not serviced.")),
            null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        try
        {
            Native.CFRunLoopAddTimer(Native.CFRunLoopGetCurrent(), timer, mode);
            Assert.Equal(42, MacRunLoop.Run(completion.Task));
        }
        finally
        {
            Native.CFRunLoopTimerInvalidate(timer);
            Native.CFRelease(timer);
            GC.KeepAlive(callback);
            NativeLibrary.Free(framework);
        }
    }

    [Fact]
    public void CompletedOperationPreservesItsResultAndFailure()
    {
        Assert.Equal(7, MacRunLoop.Run(Task.FromResult(7)));
        var failure = new InvalidOperationException("Sampling failed");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            MacRunLoop.Run(Task.FromException<int>(failure))));
    }

    private static class Native
    {
        public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void TimerCallback(nint timer, nint context);

        [DllImport(CoreFoundation)]
        public static extern double CFAbsoluteTimeGetCurrent();

        [DllImport(CoreFoundation)]
        public static extern nint CFRunLoopGetCurrent();

        [DllImport(CoreFoundation)]
        public static extern nint CFRunLoopTimerCreate(nint allocator, double fireDate, double interval,
            nuint flags, nint order, TimerCallback callback, nint context);

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopAddTimer(nint loop, nint timer, nint mode);

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopTimerInvalidate(nint timer);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(nint value);
    }
}
