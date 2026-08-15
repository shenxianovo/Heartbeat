using Serilog;

namespace Heartbeat.Desktop.Mac.Native;

public interface IMacApplicationLocator
{
    void RevealFromUser();
}

/// <summary>
/// Resolves the running executable back to its containing app bundle when packaged,
/// while still making an unbundled development executable discoverable in Finder.
/// </summary>
public sealed class MacApplicationLocator : IMacApplicationLocator
{
    private const string BundleExecutableMarker = ".app/Contents/MacOS/";
    private readonly IMacCommandRunner _commandRunner;
    private readonly Func<string?> _processPath;

    public MacApplicationLocator(IMacCommandRunner commandRunner)
        : this(commandRunner, () => Environment.ProcessPath) { }

    internal MacApplicationLocator(
        IMacCommandRunner commandRunner,
        Func<string?> processPath)
    {
        _commandRunner = commandRunner;
        _processPath = processPath;
    }

    public void RevealFromUser()
    {
        var target = ResolveRevealTarget(_processPath());
        if (target == null)
        {
            Log.Warning("无法确定 Heartbeat 当前运行位置");
            return;
        }

        var result = _commandRunner.Run("/usr/bin/open", ["-R", target]);
        if (result.ExitCode != 0)
            Log.Warning("在 Finder 中显示 Heartbeat 失败: {Error}", result.StandardError);
    }

    internal static string? ResolveRevealTarget(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        var marker = processPath.IndexOf(BundleExecutableMarker, StringComparison.OrdinalIgnoreCase);
        return marker < 0
            ? processPath
            : processPath[..(marker + ".app".Length)];
    }
}
