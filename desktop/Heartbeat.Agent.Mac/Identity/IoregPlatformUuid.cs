using System.Text.RegularExpressions;
using Heartbeat.Agent.Mac.Native;

namespace Heartbeat.Agent.Mac.Identity;

public sealed partial class IoregPlatformUuid(IMacCommandRunner commands) : IMacPlatformUuid
{
    public string? Read()
    {
        var result = commands.Run(
            "/usr/sbin/ioreg",
            ["-rd1", "-c", "IOPlatformExpertDevice"]);
        return result.ExitCode == 0 ? Parse(result.StandardOutput) : null;
    }

    public static string? Parse(string output)
    {
        var match = PlatformUuidPattern().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("\\\"IOPlatformUUID\\\"\\s*=\\s*\\\"([^\\\"]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformUuidPattern();
}
