using System.Runtime.Versioning;

namespace Heartbeat.Collector.Desktop.Windows;

public static class WindowsTarget
{
    [SupportedOSPlatform("windows")]
    public static string Read()
    {
        const uint provider = 0x52534d42; // RSMB
        var size = WindowsNative.GetSystemFirmwareTable(provider, 0, null, 0);
        if (size is < 8 or > 1_048_576) throw new IOException("无法读取本机 SMBIOS Target。");
        var buffer = new byte[size];
        if (WindowsNative.GetSystemFirmwareTable(provider, 0, buffer, size) != size)
            throw new IOException("读取本机 SMBIOS Target 失败。");
        return Parse(buffer);
    }

    internal static string Parse(ReadOnlySpan<byte> data)
    {
        RequireHeader(data);
        var offset = 8;
        while (offset + 4 <= data.Length)
        {
            var length = data[offset + 1];
            if (length < 4 || offset + length > data.Length) break;
            if (data[offset] == 1 && length >= 24) return RequireUuid(data.Slice(offset + 8, 16));
            offset = SkipStrings(data, offset + length);
        }
        throw new InvalidDataException("固件未提供可用的系统 UUID，不能确认本机 Target。");
    }

    private static void RequireHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || data[1] < 2 || (data[1] == 2 && data[2] < 6))
            throw new InvalidDataException("Target 需要 SMBIOS 2.6 或更新版本的系统 UUID。");
    }

    private static int SkipStrings(ReadOnlySpan<byte> data, int offset)
    {
        while (offset + 1 < data.Length && (data[offset] != 0 || data[offset + 1] != 0)) offset++;
        return offset + 2;
    }

    private static string RequireUuid(ReadOnlySpan<byte> bytes)
    {
        var uuid = new Guid(bytes);
        if (uuid == Guid.Empty || uuid == new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"))
            throw new InvalidDataException("固件系统 UUID 无效，不能确认本机 Target。");
        return uuid.ToString("D");
    }
}
