using System.Diagnostics;

namespace Heartbeat.Desktop.Mac.Native;

public sealed record MacCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>macOS system-tool process lifecycle shared by native adapters.</summary>
public interface IMacCommandRunner
{
    MacCommandResult Run(string fileName, IReadOnlyList<string> arguments);
}

public sealed class MacCommandRunner : IMacCommandRunner
{
    public MacCommandResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process == null)
            return new MacCommandResult(-1, string.Empty, "Process could not be started.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        return new MacCommandResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }
}
