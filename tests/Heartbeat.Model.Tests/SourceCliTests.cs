using System.Diagnostics;
using System.Text.Json;
using Heartbeat.Observers.Sources;

namespace Heartbeat.Model.Tests;

public sealed class SourceCliTests
{
    [Test]
    public async Task InvalidNativeInputExitsCleanlyAndOfflineReplayIsStable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "source-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var state = Path.Combine(directory, "state.sqlite");
            var invalid = await Run("--source", "process", "--input", "abc", "--scope", "test", "--state", state, "--offline");
            await Assert.That(invalid.Code).IsEqualTo(1);
            await Assert.That(invalid.Error.Contains("Unhandled exception", StringComparison.Ordinal)).IsFalse();
            var fixture = Path.Combine(AppContext.BaseDirectory, "NativeFixtures", "Communication", "slack-create.json");
            var arguments = new[] { "--source", "slack-events", "--input", fixture, "--scope", "fixture-team", "--state", state, "--offline" };
            var first = await Run(arguments); var second = await Run(arguments);
            await Assert.That(first.Code).IsEqualTo(0); await Assert.That(second.Code).IsEqualTo(0);
            using var a = JsonDocument.Parse(first.Output); using var b = JsonDocument.Parse(second.Output);
            await Assert.That(a.RootElement.GetProperty("receipts")[0].GetProperty("observationId").GetString())
                .IsEqualTo(b.RootElement.GetProperty("receipts")[0].GetProperty("observationId").GetString());
            await Assert.That(b.RootElement.GetProperty("receipts")[0].GetProperty("replayed").GetBoolean()).IsTrue();
            var address = await Run("--drain", "--state", state, "--api", "not-a-uri");
            await Assert.That(address.Code).IsEqualTo(1);
            await Assert.That(address.Error.Contains("Unhandled exception", StringComparison.Ordinal)).IsFalse();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<(int Code, string Output, string Error)> Run(params string[] arguments)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(SourceStore).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); return (process.ExitCode, await output, await error); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
}
