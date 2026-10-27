using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

// Business translation only: durability, version identities, dependencies and delivery belong to the SDK.
internal sealed class ForegroundStore(ObserverClient sdk, EntityId observerId, ForegroundRegistration registration)
{
    internal void Checkpoint(ForegroundCapture active, ForegroundCapture? completed) => sdk.Execute(local =>
    {
        if (completed is not null)
        {
            Update(local, completed);
            local.Complete(completed.ObservationId);
        }
        Update(local, active);
    });

    internal void FinishActive() => sdk.Execute(local =>
    {
        foreach (var draft in local.Versions().Where(version => version.State == SubmissionState.Draft && version.Version.Content is Observation))
            local.Complete(draft.Version.EntityId);
    });

    private void Update(LocalTransaction local, ForegroundCapture capture)
    {
        if (local.Latest(capture.Content.Id) is null)
        {
            if (local.Latest(capture.Application.Id) is null)
                local.Draft(capture.Application.Id, ApplicationContent(capture.Application), registration.ApplicationSchema);
            var application = local.Reference(capture.Application.Id);
            local.Draft(capture.Content.Id, new BusinessContent(JsonSerializer.SerializeToElement(new
            {
                localApplicationVersionId = application.Value, name = capture.Content.Name,
            })), registration.PeriodSchema, SubmissionStrategy.OnCompletion);
        }
        var content = local.Reference(capture.Content.Id);
        var observer = local.Reference(observerId);
        local.Draft(capture.ObservationId, new Observation(observer, content, null, capture.StartAt, capture.EndAt, capture.TimeZone),
            strategy: SubmissionStrategy.OnCompletion, publishDraft: true);
    }

    internal static BusinessContent ApplicationContent(LocalApplication application) =>
        new(JsonSerializer.SerializeToElement(new { identifiers = application.Identifiers }, JsonSerializerOptions.Web));
}
