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
            new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000014"),
                new ApplicationContent(Ref.Recognize(ForegroundRegistration.ApplicationSchemaId, capture.Application.Key), device.Ref,
                    capture.Application.Identifiers.Where(i => i.Scheme == "macos.bundle-id")
                        .Select(i => Ref.Recognize(Guid.Parse("019e1bbb-ec00-7000-8000-000000000015"), new(null, i.Scheme, i.Value))).ToArray(),
                    capture.Application.Identifiers.SingleOrDefault(i => i.Scheme == "macos.executable-path")?.Value),
                "direct", capture.Application.Key with { Scheme = "macos.installation-description" }),
            new(ForegroundRegistration.PeriodSchemaId,
                new PeriodContent(Ref.Recognize(ForegroundRegistration.ApplicationSchemaId, capture.Application.Key), capture.Content.Name, capture.Content.Icon),
                "direct", Time: new(capture.StartAt, capture.EndAt, capture.TimeZone)) ];
        try { data.WriteAllAsync(writes).GetAwaiter().GetResult(); }
        catch (HttpRequestException exception)
        { diagnostic($"The {capture.Content.Name ?? "foreground"} period was not saved: {exception.Message}"); }
    }
}

internal sealed record ApplicationContent(Ref LocalApplicationId, Ref DeviceId, Ref[] IdentifierIds, string? ExecutablePath);
internal sealed record PeriodContent(Ref LocalApplicationId, string? Name, BinaryContent? Icon);
