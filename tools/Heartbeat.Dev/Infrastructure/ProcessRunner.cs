using System.Diagnostics;

namespace Heartbeat.Dev;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

internal sealed class CapturedProcessCancelledException(string stdout, string stderr, CancellationToken token)
    : OperationCanceledException("Captured process was cancelled.", token)
{
    public string StdOut { get; } = stdout;
    public string StdErr { get; } = stderr;
}

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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new CapturedProcessCancelledException(await stdout, await stderr, cancellationToken);
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    internal static Process Start(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        bool redirectOutput, bool redirectInput = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
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
