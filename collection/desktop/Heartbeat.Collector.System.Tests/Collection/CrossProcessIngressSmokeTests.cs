using System.Diagnostics;

namespace Heartbeat.Collector.System.Tests.Collection;

public sealed class CrossProcessIngressSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-system-cross-process-{Guid.NewGuid():N}");

    [Fact]
    public async Task CrashThenTwoIndependentRestartsReplayProtocolDrainAndDoNotResurrectRemainder()
    {
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "collector-runtime.json");
        var segmentId = Guid.Parse("0198f7d2-2c00-7a11-8f12-010101010101");
        var inputId = Guid.Parse("0198f7d2-2c01-7a11-8f12-020202020202");
        var rejectedInputId = Guid.Parse("0198f7d2-2c02-7a11-8f12-030303030303");
        var harnessPath = FindHarnessPath();

        Assert.True(File.Exists(harnessPath), $"Cross-process harness was not built: {harnessPath}");

        var crashed = await RunHarnessAsync(
            harnessPath,
            "protocol-crash",
            statePath,
            segmentId.ToString(),
            inputId.ToString(),
            rejectedInputId.ToString());
        Assert.NotEqual(0, crashed.ExitCode);
        var prefix = "protocol-blocked-before-crash:";
        var line = Assert.Single(crashed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(prefix, line, StringComparison.Ordinal);
        var instanceId = Guid.Parse(line[prefix.Length..]);

        var replayed = await RunHarnessAsync(
            harnessPath,
            "protocol-drain",
            statePath,
            instanceId.ToString(),
            segmentId.ToString(),
            inputId.ToString());
        Assert.Equal(0, replayed.ExitCode);
        Assert.Contains(
            "protocol-drained:Drained:0:0:True:Completed",
            replayed.StandardOutput,
            StringComparison.Ordinal);

        var verified = await RunHarnessAsync(
            harnessPath,
            "protocol-verify",
            statePath,
            instanceId.ToString(),
            segmentId.ToString(),
            inputId.ToString());
        Assert.Equal(0, verified.ExitCode);
        Assert.Contains("protocol-restart-no-remainder", verified.StandardOutput, StringComparison.Ordinal);
    }

    private static string FindHarnessPath()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = testOutput.Name;
        var configuration = testOutput.Parent?.Name
            ?? throw new InvalidOperationException("Test configuration directory is unavailable.");
        var desktopDirectory = testOutput.Parent?.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("Desktop source directory is unavailable.");
        return Path.Combine(
            desktopDirectory.FullName,
            "Heartbeat.Collector.System.CrashReplayHarness",
            "bin",
            configuration,
            targetFramework,
            "Heartbeat.Collector.System.CrashReplayHarness.dll");
    }

    private static async Task<ProcessResult> RunHarnessAsync(string harnessPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(harnessPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cross-process harness did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Cross-process harness exceeded 15 seconds.");
        }
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
