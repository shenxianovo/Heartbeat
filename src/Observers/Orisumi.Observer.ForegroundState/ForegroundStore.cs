using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal sealed class ForegroundStore(IObserverSession session, EntityId observerId, ForegroundRegistration registration)
{
    private EntityId? _activeContent;

    internal void Checkpoint(ForegroundCapture active, ForegroundCapture? completed)
    {
        var operations = new List<object>();
        var contents = new Dictionary<string, BinaryContent>();
        if (completed is not null)
        {
            Update(operations, contents, completed, "old");
            operations.Add(new { op = "complete", entityId = completed.ObservationId.Value });
        }
        Update(operations, contents, active, "new");
        Commit(operations, contents.Values);
        _activeContent = active.Content.Id;
    }

    internal void FinishActive()
    {
        var result = session.RequestAsync("local", JsonLines.Json(new { draftsOnly = true, category = "observation" })).GetAwaiter().GetResult();
        var ids = result.GetProperty("versions").EnumerateArray().Select(value => value.GetProperty("version").GetProperty("entityId").GetGuid()).ToArray();
        foreach (var batch in ids.Chunk(512)) Commit(batch.Select(entityId => (object)new { op = "complete", entityId }).ToList());
    }

    private void Commit(List<object> operations, IEnumerable<BinaryContent>? contents = null) => session.RequestAsync("commit", JsonLines.Json(new { operations, contents = (contents ?? []).Select(content => content.Payload) }), Guid.NewGuid().ToString()).GetAwaiter().GetResult();

    private void Update(List<object> operations, Dictionary<string, BinaryContent> contents, ForegroundCapture capture, string prefix)
    {
        if (_activeContent != capture.Content.Id)
        {
            if (capture.Content.Icon is { } icon) contents[icon.Digest] = icon;
            operations.Add(new { op = "draft", entityId = capture.Application.Id.Value, category = "business", ifAbsent = true,
                schemaVersionId = registration.ApplicationSchema.Value, content = ApplicationContent(capture.Application).Fields });
            operations.Add(new { op = "reference", entityId = capture.Application.Id.Value, @as = prefix + "Application" });
            operations.Add(new { op = "draft", entityId = capture.Content.Id.Value, category = "business", ifAbsent = true,
                schemaVersionId = registration.PeriodSchema.Value, strategy = "OnCompletion",
                content = new { localApplicationVersionId = Guid.Empty, name = capture.Content.Name, icon = capture.Content.Icon?.Descriptor },
                bindings = new Dictionary<string, string> { ["/localApplicationVersionId"] = prefix + "Application" } });
        }
        operations.Add(new { op = "reference", entityId = capture.Content.Id.Value, @as = prefix + "Content" });
        operations.Add(new { op = "reference", entityId = observerId.Value, @as = prefix + "Observer" });
        operations.Add(new { op = "draft", entityId = capture.ObservationId.Value, category = "observation", strategy = "OnCompletion", publishPreview = true,
            content = new { observerVersionId = Guid.Empty, contentVersionId = Guid.Empty, inputSetVersionId = (Guid?)null,
                capture.StartAt, capture.EndAt, capture.TimeZone },
            bindings = new Dictionary<string, string> { ["/observerVersionId"] = prefix + "Observer", ["/contentVersionId"] = prefix + "Content" } });
    }

    internal static BusinessContent ApplicationContent(LocalApplication application) =>
        new(JsonSerializer.SerializeToElement(new { identifiers = application.Identifiers }, JsonSerializerOptions.Web));
}
