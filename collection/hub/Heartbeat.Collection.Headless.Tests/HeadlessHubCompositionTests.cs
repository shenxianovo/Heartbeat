using System.Text.Json;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless.Tests;

public sealed class HeadlessHubCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-headless-composition-{Guid.NewGuid():N}");

    [Fact]
    public void Composition_IsHeadlessAndStopsManagedCollectorBeforeTerminalUpload()
    {
        Directory.CreateDirectory(_directory);
        using var config = JsonDocument.Parse("{}");
        var accountSubjectId = Guid.CreateVersion7();
        var options = new HeadlessHubOptions
        {
            ApiKey = "test-key",
            DataDirectory = _directory,
            PackageDirectory = Path.Combine(_directory, "package"),
            SubjectId = accountSubjectId,
            HubHardwareId = "headless-runtime-machine",
            HubName = "headless-runtime",
            Config = config.RootElement.Clone()
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHeartbeatHeadlessHub(options);
        using var host = builder.Build();

        var hosted = host.Services.GetServices<IHostedService>().ToList();
        var uploadIndex = hosted.FindIndex(service => service is UploadWorker);
        var collectorIndex = hosted.FindIndex(service => service is ManagedCollectorHostedService);
        Assert.True(uploadIndex >= 0);
        Assert.True(collectorIndex > uploadIndex);
        Assert.NotNull(host.Services.GetRequiredService<UploadStream<ActivitySegmentItem>>());
        Assert.NotNull(host.Services.GetRequiredService<UploadStream<InputEventItem>>());
        var identity = host.Services.GetRequiredService<IDeviceIdentity>();
        Assert.Equal("headless-runtime-machine", identity.HardwareId);
        Assert.NotEqual(accountSubjectId.ToString("D"), identity.HardwareId);

        var references = typeof(HeadlessHubComposition).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        Assert.DoesNotContain(references, name => name?.StartsWith("Heartbeat.Desktop", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("Heartbeat.Collector.System", references);
        Assert.DoesNotContain(references, name => name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(references, name => name?.StartsWith("Velopack", StringComparison.Ordinal) == true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
