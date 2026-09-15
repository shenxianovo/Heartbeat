using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Heartbeat.Collector.Desktop.Mac;

/// <summary>Monotonic elapsed time that includes system sleep.</summary>
internal sealed class MacContinuousTimeProvider : TimeProvider
{
    // Darwin clock_gettime(3): MONOTONIC_RAW includes sleep and ignores wall-clock
    // adjustments. .NET's CLOCK_UPTIME_RAW (8) excludes sleep on macOS.
    private const int ClockMonotonicRaw = 4;
    private readonly TimeProvider _wallClock;
    private readonly Func<int, ulong> _readNanoseconds;

    public MacContinuousTimeProvider() : this(System, ReadNativeNanoseconds)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The continuous macOS clock requires macOS.");
        }
    }

    internal MacContinuousTimeProvider(TimeProvider wallClock, Func<int, ulong> readNanoseconds)
    {
        _wallClock = wallClock;
        _readNanoseconds = readNanoseconds;
    }

    public override long TimestampFrequency => 1_000_000_000;

    public override DateTimeOffset GetUtcNow() => _wallClock.GetUtcNow();

    public override long GetTimestamp() => checked((long)_readNanoseconds(ClockMonotonicRaw));

    private static ulong ReadNativeNanoseconds(int clockId)
    {
        var timestamp = ClockGetTimeNanoseconds(clockId);
        if (timestamp == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the continuous macOS clock.");
        }

        return timestamp;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "clock_gettime_nsec_np", SetLastError = true)]
    private static extern ulong ClockGetTimeNanoseconds(int clockId);
}
