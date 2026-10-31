using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

/// <summary>Keeps the ongoing period in memory and submits each period once it ends.</summary>
internal sealed class ForegroundStore(IObserverSession session, EntityId observerId, ForegroundRegistration registration, Action<string> diagnostic)
{
    private ForegroundCapture? _active;

    internal void Checkpoint(ForegroundCapture active, ForegroundCapture? completed)
    {
        if (completed is not null) Submit(completed);
        _active = active;
    }

    internal void FinishActive()
    {
        if (_active is not { } active) return;
        _active = null;
        Submit(active);
    }

    private void Submit(ForegroundCapture capture)
    {
        var operations = new List<object>
        {
            new { op = "write", entityId = capture.Application.Id.Value, category = "business", ifAbsent = true,
                schemaVersionId = registration.ApplicationSchema.Value, content = ApplicationContent(capture.Application).Fields, @as = "application" },
            new { op = "write", entityId = capture.Content.Id.Value, category = "business", previousVersionId = (Guid?)null,
                schemaVersionId = registration.PeriodSchema.Value,
                content = new { localApplicationVersionId = Guid.Empty, name = capture.Content.Name, icon = capture.Content.Icon?.Descriptor },
                bindings = new Dictionary<string, string> { ["/localApplicationVersionId"] = "application" }, @as = "content" },
            new { op = "reference", entityId = observerId.Value, @as = "observer" },
            new { op = "write", entityId = capture.ObservationId.Value, category = "observation", previousVersionId = (Guid?)null,
                content = new { observerVersionId = Guid.Empty, contentVersionId = Guid.Empty, inputSetVersionId = (Guid?)null,
                    capture.StartAt, capture.EndAt, capture.TimeZone },
                bindings = new Dictionary<string, string> { ["/observerVersionId"] = "observer", ["/contentVersionId"] = "content" } },
        };
        JsonElement[] contents = capture.Content.Icon is { } icon ? new[] { icon.Payload } : [];
        try { session.RequestAsync("commit", JsonLines.Json(new { operations, contents })).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is HttpRequestException or SubmissionRejectedException or InvalidOperationException or IOException)
        { diagnostic($"The {capture.Content.Name ?? "foreground"} period was not saved: {exception.Message}"); }
    }

    internal static BusinessContent ApplicationContent(LocalApplication application) =>
        new(JsonSerializer.SerializeToElement(new { identifiers = application.Identifiers }, JsonSerializerOptions.Web));
}
