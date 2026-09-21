using System.Diagnostics;

namespace Heartbeat.Dev;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

internal interface IProcessRunner
{
    // Opens a user-owned GUI application without attaching its lifetime to the CLI.
    void OpenApplication(string path) => throw new NotSupportedException();

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
    public void OpenApplication(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        });
    }

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

    /// 启动一个由 CLI 管理的 .NET 子进程，输出被重定向以便留证。
    internal static Process StartManaged(
        string workingDirectory,
        string assembly,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment) =>
        Start(workingDirectory, "dotnet", [assembly, .. arguments], environment, redirectOutput: true);

    /// 先发 SIGINT 让子进程自己收尾（写完产物、停掉观测），超时才强杀。
    internal static async Task InterruptAsync(
        Process process,
        TimeSpan timeoutAfter,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);
        var signalStart = new ProcessStartInfo("kill");
        signalStart.ArgumentList.Add("-INT");
        signalStart.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var signal = Process.Start(signalStart);
        if (signal is not null) await signal.WaitForExitAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    private static void Kill(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
}
