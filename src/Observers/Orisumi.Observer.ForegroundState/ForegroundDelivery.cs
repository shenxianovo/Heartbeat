using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal sealed class ForegroundDelivery(ObserverClient sdk, LocalApplicationRegistry applications,
    ForegroundRegistration registration, TextWriter output)
{
    private string? _reported;

    internal async Task SendAsync(CancellationToken token)
    {
        await sdk.SendAsync(token);
        var rejection = sdk.Status.Rejection;
        if (rejection is null) { Report(); return; }
        var rejected = rejection.Version.Version;
        try
        {
            if (rejection.Version.Rule == "VERSION-6" && rejected.PreviousVersionId is null)
            {
                var current = await sdk.ReadCurrentAsync(rejected.EntityId, token);
                if (current is { PreviousVersionId: null } && VersionRules.SameSubmission(rejected,
                    current.Submission with { VersionId = rejected.VersionId }))
                {
                    sdk.Execute(local => local.AdoptSaved(current));
                    await sdk.SendAsync(token);
                }
            }
            else if (rejection.Version.Rule is "HISTORY-7" or "ENTITY-6")
                await ReplaceDeletedApplicationAsync(rejected, token);
        }
        catch (HttpRequestException) { /* The rejection stays in the status; the next round retries. */ }
        Report();
    }

    private async Task ReplaceDeletedApplicationAsync(VersionSubmission rejected, CancellationToken token)
    {
        var application = sdk.Execute(local =>
        {
            var schema = rejected.SchemaVersionId is { } id ? local.Find(id)?.Version.EntityId : null;
            if (schema == ForegroundRegistration.ApplicationSchemaId) return local.Find(rejected.VersionId);
            if (schema != ForegroundRegistration.PeriodSchemaId || rejected.Content is not BusinessContent content) return null;
            return local.Find(new(content.Fields.GetProperty("localApplicationVersionId").GetGuid()));
        });
        if (application is null) return;
        // A missing reference may be unrelated to an application deletion. Confirm that deletion first.
        using var response = await sdk.GetEntityResponseAsync(application.Version.EntityId, token);
        if (response.StatusCode != HttpStatusCode.Gone) return;
        var identifiers = ((BusinessContent)application.Version.Content).Fields.GetProperty("identifiers")
            .Deserialize<ApplicationIdentifier[]>(JsonSerializerOptions.Web)!;
        var replacement = applications.ReplaceDeleted(new(application.Version.EntityId, identifiers));
        sdk.Execute(local =>
        {
            if (rejected.VersionId == application.Version.VersionId)
                local.ReplaceRejected(replacement.Id, ForegroundStore.ApplicationContent(replacement), local.Resolve(registration.ApplicationSchema));
            else
            {
                if (local.Latest(replacement.Id) is null)
                    local.Draft(replacement.Id, ForegroundStore.ApplicationContent(replacement), registration.ApplicationSchema);
                var reference = local.Reference(replacement.Id);
                local.ReplaceReferenceAndResume(application.Version.VersionId, reference);
            }
        });
        await sdk.SendAsync(token);
    }

    private void Report()
    {
        var status = sdk.Status;
        var line = JsonSerializer.Serialize(new
        {
            type = "delivery",
            status.Pending,
            status.Drafts,
            status.Saved,
            rejection = status.Rejection is { } rejection ? new { rejection.Version.Rule, rejection.Version.Detail } : null,
            status.Error,
        }, JsonSerializerOptions.Web);
        if (line == _reported) return;
        _reported = line;
        output.WriteLine(line);
    }

    internal async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            await SendAsync(token);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
}
