using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;

namespace Heartbeat.Collection.Hub.Upload;

/// <summary>The Runtime journal is the sole durable source; this adapter stores no second queue.</summary>
public sealed class RuntimeFactUploadSource(CollectorRuntime runtime, IDeviceIdentity? machine = null,
    ICollectorFactSubjectNames? subjectNames = null)
    : IUploadSource<FactUploadItem>
{
    public DeliveryRemainder Remainder => runtime.FactUploadRemainder;
    public UploadStreamStatus StorageStatus => Remainder.RetainedLocally is > 0 and var count
        ? new UploadStreamStatus(UploadStreamState.Backlog, $"{count} durable Facts/Stream Gaps await upload.",
            "Uploads retry automatically when Analytics is available.")
        : UploadStreamStatus.Ready;

    public List<FactUploadItem> ReadBatch()
    {
        var batch = runtime.ReadPendingFacts();
        if (subjectNames is not null)
            foreach (var stream in batch.Select(item => item.Stream))
                stream.Subject.DisplayName = subjectNames.DisplayName(stream.CollectorInstanceId);
        if (machine is not null)
            foreach (var stream in batch.Select(item => item.Stream))
                if (stream.Subject.Kind == "machine" &&
                    string.Equals(stream.Subject.HardwareId, machine.HardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    stream.Subject.HardwareId = machine.HardwareId;
                    stream.Subject.DisplayName = machine.DeviceName;
                }
        return batch;
    }

    public void Confirm(IReadOnlyList<FactUploadItem> items) => runtime.ConfirmUploadedFacts(items);
}

/// <summary>Host presentation hint; it never changes the observed Subject identity.</summary>
public interface ICollectorFactSubjectNames
{
    string? DisplayName(Guid collectorInstanceId);
}
