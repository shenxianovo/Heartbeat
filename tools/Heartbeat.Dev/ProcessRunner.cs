using System.Diagnostics;

namespace Heartbeat.Dev;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

internal interface IProcessRunner
{
    Task<ProcessResult> CaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken);

    Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken);
}

internal sealed class ProcessRunner(string workingDirectory) : IProcessRunner
{
    public Task<ProcessResult> CaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken) =>
        CaptureAsync(workingDirectory, fileName, arguments, cancellationToken, environment);

    public async Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        using var process = Start(workingDirectory, fileName, arguments, environment, redirectOutput: false);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        return process.ExitCode;
    }

    internal static async Task<ProcessResult> CaptureAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var process = Start(workingDirectory, fileName, arguments, environment, redirectOutput: true);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    internal static Process Start(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        bool redirectOutput)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var item in environment)
            {
                start.Environment[item.Key] = item.Value;
            }
        }
        var process = new Process { StartInfo = start };
        process.Start();
        return process;
    }

    private static void Kill(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
}
