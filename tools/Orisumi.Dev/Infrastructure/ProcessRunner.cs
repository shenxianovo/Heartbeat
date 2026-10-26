using System.Diagnostics;

namespace Orisumi.Dev;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

internal sealed class CapturedProcessCancelledException(string stdout, string stderr, CancellationToken token)
    : OperationCanceledException("Captured process was cancelled.", token)
{
    public string StdOut { get; } = stdout;
    public string StdErr { get; } = stderr;
}

internal interface IProcessRunner
{
    async Task<int> StreamAsync(string fileName, IReadOnlyList<string> arguments, TextWriter output, CancellationToken token)
    {
        var result = await CaptureAsync(fileName, arguments, null, token);
        await output.WriteAsync(result.StdOut + result.StdErr);
        return result.ExitCode;
    }

    Task<ProcessResult> CaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken);
}

internal sealed class ProcessRunner(string workingDirectory) : IProcessRunner
{
    public async Task<int> StreamAsync(string fileName, IReadOnlyList<string> arguments, TextWriter output, CancellationToken token)
    {
        using var process = Start(workingDirectory, fileName, arguments, null);
        var writer = TextWriter.Synchronized(output);
        async Task CopyAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line) await writer.WriteLineAsync(line);
        }
        var copies = Task.WhenAll(CopyAsync(process.StandardOutput), CopyAsync(process.StandardError));
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally { await copies; }
        return process.ExitCode;
    }

    public Task<ProcessResult> CaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken) =>
        CaptureAsync(workingDirectory, fileName, arguments, cancellationToken, environment);

    internal static async Task<ProcessResult> CaptureAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var process = Start(workingDirectory, fileName, arguments, environment);
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

    private static Process Start(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
