using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Segments;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collector.System.Collection;

public sealed record SystemCollectorBindingOptions(
    string DataDirectory,
    string? PackageDirectory = null);

/// <summary>
/// Owns the BuiltIn Package, stable Instance, Runtime, and one InProcess Activation for a desktop
/// Agent. Construction is side-effect free so composition tests never open production state.
/// </summary>
public sealed class SystemCollectorHostedService(
    SystemCollectorBindingOptions options,
    ISegmentSink legacySegmentAdapter,
    IDeviceIdentity deviceIdentity,
    SystemInProcessCollector collector) : IHostedService, IDisposable, IAsyncDisposable
{
    private CollectorRuntime? _runtime;
    private InProcessCollectorActivation? _activation;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not null)
            throw new InvalidOperationException("The system Collector Binding is already started.");

        Directory.CreateDirectory(options.DataDirectory);
        var package = LocalCollectorPackage.Load(
            options.PackageDirectory ?? SystemCollectorPackage.Path);
        var runtime = CollectorRuntime.Open(
            Path.Combine(options.DataDirectory, "collector-runtime.json"),
            legacySegmentAdapter);
        try
        {
            using var config = JsonDocument.Parse("{}");
            var subject = new SubjectReference(
                MachineSubjectId(deviceIdentity.HardwareId),
                SubjectKind.Machine);
            var instance = runtime.FindInstances(package.Manifest.PackageId, subject).FirstOrDefault()
                ?? runtime.CreateInstance(
                    package,
                    subject,
                    new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
            collector.ConfigureOutbox(Path.Combine(
                options.DataDirectory,
                "system-collector-outbox.json"));
            var activation = await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                collector,
                cancellationToken);
            _runtime = runtime;
            _activation = activation;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_activation is not null)
        {
            await _activation.StopAsync(cancellationToken);
            _activation = null;
        }
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    internal static Guid MachineSubjectId(string hardwareId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        if (Guid.TryParse(hardwareId, out var parsed) && parsed != Guid.Empty)
            return parsed;
        throw new InvalidOperationException(
            "The desktop machine identity must be a non-empty UUID before the system Collector can activate.");
    }
}
