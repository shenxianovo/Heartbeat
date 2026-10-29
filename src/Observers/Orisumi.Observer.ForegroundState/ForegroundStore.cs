using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal sealed class ForegroundStore(IObserverSession session, EntityId observerId, ForegroundRegistration registration)
{
    internal void Checkpoint(ForegroundCapture active, ForegroundCapture? completed)
    {
        var operations = new List<object>();
        if (completed is not null)
        {
            Update(operations, completed, "old");
            operations.Add(new { op = "complete", entityId = completed.ObservationId.Value });
        }
        Update(operations, active, "new");
        Commit(operations);
    }

    internal void FinishActive()
    {
        var result = session.RequestAsync("local", JsonLines.Json(new { draftsOnly = true, category = "observation" })).GetAwaiter().GetResult();
        var ids = result.GetProperty("versions").EnumerateArray().Select(value => value.GetProperty("version").GetProperty("entityId").GetGuid()).ToArray();
        foreach (var batch in ids.Chunk(512)) Commit(batch.Select(entityId => (object)new { op = "complete", entityId }).ToList());
    }

    private void Commit(List<object> operations) => session.RequestAsync("commit", JsonLines.Json(new { operations }), Guid.NewGuid().ToString()).GetAwaiter().GetResult();

    private void Update(List<object> operations, ForegroundCapture capture, string prefix)
    {
        operations.Add(new { op = "draft", entityId = capture.Application.Id.Value, category = "business", ifAbsent = true,
            schemaVersionId = registration.ApplicationSchema.Value, content = ApplicationContent(capture.Application).Fields });
        operations.Add(new { op = "reference", entityId = capture.Application.Id.Value, @as = prefix + "Application" });
        operations.Add(new { op = "draft", entityId = capture.Content.Id.Value, category = "business", ifAbsent = true,
            schemaVersionId = registration.PeriodSchema.Value, strategy = "OnCompletion",
            content = new { localApplicationVersionId = Guid.Empty, name = capture.Content.Name },
            bindings = new Dictionary<string, string> { ["/localApplicationVersionId"] = prefix + "Application" } });
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
