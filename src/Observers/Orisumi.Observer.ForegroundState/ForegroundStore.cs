using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

/// <summary>Keeps the ongoing period in memory and writes it once it ends.</summary>
internal sealed class ForegroundStore(DataClient data, DeviceContext device, Action<string> diagnostic)
{
    private ForegroundCapture? _active;
    internal void Checkpoint(ForegroundCapture active, ForegroundCapture? completed)
    {
        if (completed is not null) Write(completed);
        _active = active;
    }
    internal void FinishActive()
    {
        if (_active is not { } active) return;
        _active = null;
        Write(active);
    }
    private void Write(ForegroundCapture capture)
    {
        WriteItem[] writes = [
            new(ForegroundRegistration.ApplicationSchemaId,
                new ApplicationContent(device.Ref, capture.Application.Identifiers), capture.Application.Key),
            new(ForegroundRegistration.PeriodSchemaId,
                new PeriodContent(Ref.Recognize(ForegroundRegistration.ApplicationSchemaId, capture.Application.Key), capture.Content.Name, capture.Content.Icon),
                Time: new(capture.StartAt, capture.EndAt, capture.TimeZone)) ];
        try { data.WriteAllAsync(writes).GetAwaiter().GetResult(); }
        catch (HttpRequestException exception)
        { diagnostic($"The {capture.Content.Name ?? "foreground"} period was not saved: {exception.Message}"); }
    }
}

internal sealed record ApplicationContent(Ref DeviceVersionId, IReadOnlyList<ApplicationIdentifier> Identifiers);
internal sealed record PeriodContent(Ref LocalApplicationVersionId, string? Name, BinaryContent? Icon);
