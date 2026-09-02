using Heartbeat.Desktop.UI.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.UI.Tests.Diagnostics;

public class DesktopStartupSmokeTests
{
    [Fact]
    public void TryGetRequest_RecognizesThePlainFlag()
    {
        Assert.True(DesktopStartupSmoke.TryGetRequest(["--verify-startup"], out var request));
        Assert.Null(request.ReportPath);
    }

    [Fact]
    public void TryGetRequest_RecognizesAnInlineReportPath()
    {
        Assert.True(DesktopStartupSmoke.TryGetRequest(
            ["--other", "--verify-startup=/tmp/report.json"], out var request));
        Assert.Equal("/tmp/report.json", request.ReportPath);
    }

    [Theory]
    [InlineData("--verify")]
    [InlineData("verify-startup")]
    public void TryGetRequest_IgnoresUnrelatedArguments(string argument)
    {
        Assert.False(DesktopStartupSmoke.TryGetRequest([argument], out _));
        Assert.False(DesktopStartupSmoke.TryGetRequest(null, out _));
    }

    [Fact]
    public void Run_SucceedsWhenTheHostStartsAndStops()
    {
        var host = new FakeHost();
        var output = new StringWriter();

        var exitCode = DesktopStartupSmoke.Run(host, new DesktopStartupSmoke.Request(), output: output);

        Assert.Equal(0, exitCode);
        Assert.True(host.Started);
        Assert.True(host.Stopped);
        Assert.Contains("startup-smoke ok", output.ToString());
        // Browser 没注册时也算成功：可选 Collector 缺席不是启动失败。
        Assert.Contains("notRegistered", output.ToString());
    }

    [Fact]
    public void Run_FailsWhenStartupThrows()
    {
        var host = new FakeHost { StartFailure = new InvalidOperationException("boom") };
        var output = new StringWriter();

        var exitCode = DesktopStartupSmoke.Run(host, new DesktopStartupSmoke.Request(), output: output);

        Assert.Equal(1, exitCode);
        Assert.Contains("startup-smoke failed", output.ToString());
        Assert.Contains("boom", output.ToString());
    }

    [Fact]
    public void Run_FailsWhenStartupExceedsTheBudget()
    {
        var host = new FakeHost { StartDelay = TimeSpan.FromSeconds(30) };
        var output = new StringWriter();

        var exitCode = DesktopStartupSmoke.Run(
            host,
            new DesktopStartupSmoke.Request(),
            timeout: TimeSpan.FromMilliseconds(50),
            output: output);

        Assert.Equal(1, exitCode);
        Assert.Contains("startup-smoke failed", output.ToString());
    }

    [Fact]
    public void Run_WritesTheReportFileWhenAPathIsGiven()
    {
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            $"heartbeat-smoke-{Guid.NewGuid():N}",
            "report.json");
        try
        {
            var exitCode = DesktopStartupSmoke.Run(
                new FakeHost(),
                new DesktopStartupSmoke.Request(reportPath),
                output: new StringWriter());

            Assert.Equal(0, exitCode);
            Assert.Contains("\"hostStarted\":true", File.ReadAllText(reportPath));
        }
        finally
        {
            var directory = Path.GetDirectoryName(reportPath);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Inconclusive_ReportsAFailureInsteadOfAQuietSuccess()
    {
        var output = new StringWriter();

        var exitCode = DesktopStartupSmoke.Inconclusive(
            new DesktopStartupSmoke.Request(), "another instance", output);

        Assert.Equal(1, exitCode);
        Assert.Contains("startup-smoke inconclusive", output.ToString());
        Assert.Contains("another instance", output.ToString());
    }

    private sealed class FakeHost : IHost
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public Exception? StartFailure { get; init; }
        public TimeSpan StartDelay { get; init; } = TimeSpan.Zero;
        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (StartDelay > TimeSpan.Zero)
                await Task.Delay(StartDelay, cancellationToken);
            if (StartFailure is not null)
                throw StartFailure;
            Started = true;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public void Dispose() { }

        private sealed class EmptyServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
