using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
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
        // 没人指定目录时 smoke 自己开一个临时目录，绝不落在真实用户数据目录里。
        Assert.True(request.OwnsDataDirectory);
        Assert.StartsWith(Path.GetTempPath(), request.DataDirectory);
        Assert.NotEqual(
            Path.Combine(Path.GetTempPath(), "heartbeat-startup-smoke"),
            request.DataDirectory);
    }

    [Fact]
    public void TryGetRequest_HonoursAnExplicitDataDirectory()
    {
        var explicitDirectory = Path.Combine(Path.GetTempPath(), $"heartbeat-smoke-{Guid.NewGuid():N}");

        Assert.True(DesktopStartupSmoke.TryGetRequest(
            ["--verify-startup", $"--verify-startup-data-directory={explicitDirectory}"],
            out var request));

        Assert.Equal(Path.GetFullPath(explicitDirectory), request.DataDirectory);
        Assert.False(request.OwnsDataDirectory);
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
        using var host = new FakeHost();
        var output = new StringWriter();

        var exitCode = DesktopStartupSmoke.Run(host, new DesktopStartupSmoke.Request(), output: output);

        Assert.Equal(0, exitCode);
        Assert.True(host.Started);
        Assert.True(host.Stopped);
        Assert.Contains("startup-smoke ok", output.ToString());
        // 只断言宿主自己：System BuiltIn 在，且报告里不提任何具名可选 Collector。
        Assert.Contains("\"systemCollector\":\"registered\"", output.ToString());
        Assert.DoesNotContain("browser", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// System 是宿主唯一写死的 BuiltIn。它缺席不是"可选 Collector 不在"，是组合坏了，必须红。
    /// </summary>
    [Fact]
    public void Run_FailsWhenTheSystemBuiltInIsMissing()
    {
        using var host = new FakeHost(withSystemCollector: false);
        var output = new StringWriter();

        var exitCode = DesktopStartupSmoke.Run(host, new DesktopStartupSmoke.Request(), output: output);

        Assert.Equal(1, exitCode);
        Assert.Contains("system built-in collector runtime is not registered", output.ToString());
    }

    /// <summary>
    /// 自己开的隔离目录跑完要收干净；调用方指定的目录一律不动。
    /// </summary>
    [Fact]
    public void Run_CleansUpOnlyTheDataDirectoryItOwns()
    {
        using var owned = new FakeHost();
        var ownedRequest = new DesktopStartupSmoke.Request();
        Directory.CreateDirectory(ownedRequest.DataDirectory);
        File.WriteAllText(Path.Combine(ownedRequest.DataDirectory, "config.json"), "{}");

        Assert.Equal(0, DesktopStartupSmoke.Run(owned, ownedRequest, output: new StringWriter()));
        Assert.False(Directory.Exists(ownedRequest.DataDirectory));

        var borrowedDirectory = Path.Combine(Path.GetTempPath(), $"heartbeat-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(borrowedDirectory);
        try
        {
            using var borrowed = new FakeHost();
            var borrowedRequest = new DesktopStartupSmoke.Request(
                DataDirectoryOverride: borrowedDirectory);

            Assert.Equal(0, DesktopStartupSmoke.Run(borrowed, borrowedRequest, output: new StringWriter()));
            Assert.True(Directory.Exists(borrowedDirectory));
        }
        finally
        {
            Directory.Delete(borrowedDirectory, recursive: true);
        }
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
            using var host = new FakeHost();
            var exitCode = DesktopStartupSmoke.Run(
                host,
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
        private readonly string _runtimeRoot = Path.Combine(
            Path.GetTempPath(), $"heartbeat-smoke-host-{Guid.NewGuid():N}");
        private readonly CollectorRuntime? _systemRuntime;

        public FakeHost(bool withSystemCollector = true)
        {
            if (!withSystemCollector)
            {
                Services = new StubServiceProvider(null);
                return;
            }
            Directory.CreateDirectory(_runtimeRoot);
            _systemRuntime = CollectorRuntime.Open(
                Path.Combine(_runtimeRoot, "collector-runtime.json"),
                new DiscardingSegmentSink());
            Services = new StubServiceProvider(_systemRuntime);
        }

        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public Exception? StartFailure { get; init; }
        public TimeSpan StartDelay { get; init; } = TimeSpan.Zero;
        public IServiceProvider Services { get; }

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

        public void Dispose()
        {
            _systemRuntime?.Dispose();
            if (Directory.Exists(_runtimeRoot))
                Directory.Delete(_runtimeRoot, recursive: true);
        }

        private sealed class StubServiceProvider(CollectorRuntime? systemRuntime) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(CollectorRuntime) ? systemRuntime : null;
        }

        private sealed class DiscardingSegmentSink : ISegmentSink
        {
            public void Push(List<ActivitySegmentItem> snapshots) { }
        }
    }
}
