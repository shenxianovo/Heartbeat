using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Observer.Runtime;

/// <summary>Reads the running device for the Observer context. Null where the platform gives no stable identifier.</summary>
internal static partial class DeviceIdentity
{
    public static string? Read()
    {
        if (OperatingSystem.IsMacOS())
            return Encode("macos.hardware-uuid", HardwareUuid().Match(Output("/usr/sbin/ioreg", "-rd1", "-c", "IOPlatformExpertDevice")).Groups[1].Value,
                Output("/usr/sbin/scutil", "--get", "ComputerName").Trim(), "macos");
        if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-id"))
            return Encode("linux.machine-id", File.ReadAllText("/etc/machine-id").Trim(), Environment.MachineName, "linux");
        return null;
    }

    private static string? Encode(string scheme, string value, string name, string platform) => value.Length == 0 ? null :
        JsonSerializer.Serialize(new { key = new { scope = (string?)null, scheme, value }, name = name.Length == 0 ? null : name, platform });

    private static string Output(string file, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : "";
    }

    [GeneratedRegex("\"IOPlatformUUID\" = \"([^\"]+)\"")]
    private static partial Regex HardwareUuid();
}
