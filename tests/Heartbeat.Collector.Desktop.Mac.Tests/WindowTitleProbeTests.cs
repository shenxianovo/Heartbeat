using System.Text.Json;
using Heartbeat.Collector.Desktop.Mac.Diagnostics;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class WindowTitleProbeTests
{
    private static readonly ForegroundApplication Player = new("macos", "bundle_id", "com.example.Player", "Player");

    [Fact]
    public void ProbeDefaultsToTwoMinutesOfQuarterSecondPolling()
    {
        var options = WindowTitleProbeOptions.Parse(["--probe-window-titles", "--output", "readings.json"]);

        Assert.Equal(TimeSpan.FromSeconds(120), options.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.PollInterval);
        Assert.False(options.IncludeTitles);
        Assert.True(Path.IsPathRooted(options.OutputPath));
    }

    [Theory]
    [InlineData("--probe-window-titles")]
    [InlineData("--probe-window-titles", "--output")]
    [InlineData("--probe-window-titles", "--output", "readings.json", "--duration-seconds", "0")]
    [InlineData("--probe-window-titles", "--output", "readings.json", "--poll-milliseconds", "20")]
    [InlineData("--probe-window-titles", "--output", "readings.json", "--sample-everything")]
    public void ProbeRefusesArgumentsItCannotHonour(params string[] args)
    {
        Assert.ThrowsAny<ArgumentException>(() => WindowTitleProbeOptions.Parse(args));
    }

    [Fact]
    public async Task ProbeRecordsBothNotifiedAndPolledReadingsWithoutTitleText()
    {
        var source = new ProbeSource(new DesktopActivitySample(Player, "Track One"));
        var readings = await RunAsync(source, includeTitles: false, before: probeSource =>
            probeSource.Emit(new DesktopObservation.Activity(new DesktopActivitySample(Player, "Track Two"))));

        Assert.Contains(readings, reading => reading.GetProperty("origin").GetString() == "event");
        Assert.Contains(readings, reading => reading.GetProperty("origin").GetString() == "poll");
        Assert.All(readings, reading => Assert.Null(reading.GetProperty("title").GetString()));
        Assert.All(readings, reading => Assert.NotNull(reading.GetProperty("titleHash").GetString()));
        Assert.Contains(readings, reading => reading.GetProperty("titleLength").GetInt32() == "Track Two".Length);
    }

    [Fact]
    public async Task ProbeKeepsTitleTextOnlyWhenTheOperatorAsksForIt()
    {
        var source = new ProbeSource(new DesktopActivitySample(Player, "Track One"));

        var readings = await RunAsync(source, includeTitles: true);

        Assert.All(readings, reading => Assert.Equal("Track One", reading.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task ProbeRecordsCapabilityOutagesBesideTheReadings()
    {
        var source = new ProbeSource(new DesktopActivitySample(Player, "Track One"));

        var document = await CaptureAsync(source, includeTitles: false, before: probeSource =>
            probeSource.Emit(new DesktopObservation.Capability(new CapabilityObservation(
                ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_starting"))));

        var capability = Assert.Single(document.RootElement.GetProperty("capabilities").EnumerateArray());
        Assert.Equal("WindowTitle", capability.GetProperty("capability").GetString());
        Assert.Equal("Unavailable", capability.GetProperty("state").GetString());
        Assert.Equal("observer_starting", capability.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("Now Playing - A Song", "ow Playing - A SongN", 0, 0, true)]
    [InlineData("Downloading 41%", "Downloading 42%", 13, 1, false)]
    [InlineData("Report.txt - Editor", "Notes.txt - Editor", 0, 13, false)]
    public void TitleShapeExposesMarqueeAndProgressWithoutTheText(
        string previous,
        string current,
        int commonPrefix,
        int commonSuffix,
        bool rotation)
    {
        var shape = TitleShape.Between(previous, current);

        Assert.Equal(commonPrefix, shape.CommonPrefix);
        Assert.Equal(commonSuffix, shape.CommonSuffix);
        Assert.Equal(rotation, shape.IsRotation);
    }

    private static async Task<JsonElement[]> RunAsync(
        ProbeSource source,
        bool includeTitles,
        Action<ProbeSource>? before = null)
    {
        using var document = await CaptureAsync(source, includeTitles, before);
        return [.. document.RootElement.GetProperty("readings").EnumerateArray().Select(reading => reading.Clone())];
    }

    private static async Task<JsonDocument> CaptureAsync(
        ProbeSource source,
        bool includeTitles,
        Action<ProbeSource>? before = null)
    {
        var directory = Directory.CreateTempSubdirectory("heartbeat-probe");
        try
        {
            var output = Path.Combine(directory.FullName, "readings.json");
            var options = new WindowTitleProbeOptions(
                TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), output, includeTitles);
            var probe = new WindowTitleProbe(source, TimeProvider.System);
            var run = probe.RunAsync(options, TextWriter.Null, CancellationToken.None);
            before?.Invoke(source);
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
            return JsonDocument.Parse(await File.ReadAllTextAsync(output));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class ProbeSource(DesktopActivitySample sample) : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation;

        public void Emit(DesktopObservation observation) => Observation?.Invoke(observation);

        public DesktopSnapshot Capture() => new(sample, []);

        public void RefreshCapabilities() { }

        public void StartObserving() { }

        public void StopObserving() { }

        public void Dispose() { }
    }
}
