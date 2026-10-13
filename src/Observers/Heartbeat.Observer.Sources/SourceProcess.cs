using System.Diagnostics;
using System.Text;

namespace Heartbeat.Observers.Sources;

internal static class SourceProcess
{
    internal static async Task<byte[]> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var error = ReadBoundedAsync(process.StandardError.BaseStream, timeout.Token);
            var bytes = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout.Token);
            var exited = process.WaitForExitAsync(timeout.Token);
            var tasks = new List<Task> { error, bytes, exited };
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks);
                await completed; // Fail as soon as either bounded reader fails; do not wait on a blocked child.
                tasks.Remove(completed);
            }
            if (process.ExitCode != 0)
            {
                var detail = Encoding.UTF8.GetString(await error);
                throw new InvalidDataException($"{executable} failed (exit {process.ExitCode}): {detail[..Math.Min(detail.Length, 512)]}");
            }
            return await bytes;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException($"{executable} exceeded the 30 second source-read timeout.", error);
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (memory.Length + count > SourceJson.MaximumInputBytes) throw new InvalidDataException("Source command output exceeds 8 MiB.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    internal static string StrictText(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
}
