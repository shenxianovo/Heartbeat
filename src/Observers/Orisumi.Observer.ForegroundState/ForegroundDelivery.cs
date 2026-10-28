using System.Net;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

/// <summary>Business repair of rejections; the Runtime owns the sending loop.</summary>
internal sealed class ForegroundDelivery(IObserverSession session, LocalApplicationRegistry applications,
    ForegroundRegistration registration, TextWriter output)
{
    private string? _reported;
    internal async Task SendAsync(CancellationToken token)
    {
        var status = await RequestAsync("status", new { }, token);
        if (status.GetProperty("rejection") is { ValueKind: not JsonValueKind.Null } rejection)
        {
            var local = rejection.GetProperty("version");
            var rejected = VersionJson.ReadSubmission(local.GetProperty("version"));
            var rule = local.GetProperty("rule").GetString();
            try
            {
                if (rule == "VERSION-6" && rejected.PreviousVersionId is null)
                {
                    var read = await RequestAsync("readCurrent", new { entityId = rejected.EntityId.Value }, token);
                    if (read.GetProperty("version") is { ValueKind: not JsonValueKind.Null } saved)
                    {
                        var current = VersionJson.ReadSaved(saved);
                        if (current.PreviousVersionId is null && VersionRules.SameSubmission(rejected, current.Submission with { VersionId = rejected.VersionId }))
                            await RequestAsync("repair", new { action = "adoptSaved", rejectedVersionId = rejected.VersionId.Value, saved }, token);
                    }
                }
                else if (rule is "HISTORY-7" or "ENTITY-6") await ReplaceDeletedApplicationAsync(rejected, token);
            }
            catch (HttpRequestException) { }
                status = await RequestAsync("status", new { }, token);
        }
        var line = JsonSerializer.Serialize(new { type = "delivery", pending = status.GetProperty("pending"),
            drafts = status.GetProperty("drafts"), saved = status.GetProperty("saved"),
            rejection = status.GetProperty("rejection"), error = status.GetProperty("error") });
        if (line != _reported) { _reported = line; await output.WriteLineAsync(line); }
    }

    private async Task ReplaceDeletedApplicationAsync(VersionSubmission rejected, CancellationToken token)
    {
        if (rejected.SchemaVersionId is not { } schemaId) return;
        var schema = await FindAsync(schemaId, token);
        VersionSubmission? application = null;
        if (schema?.EntityId == ForegroundRegistration.ApplicationSchemaId) application = rejected;
        else if (schema?.EntityId == ForegroundRegistration.PeriodSchemaId && rejected.Content is BusinessContent content)
        {
            var version = await FindAsync(new(content.Fields.GetProperty("localApplicationVersionId").GetGuid()), token);
            if (version is not null) application = version;
        }
        if (application is null) return;
        var response = await RequestAsync("entityStatus", new { entityId = application.EntityId.Value }, token);
        if (response.GetProperty("status").GetInt32() != (int)HttpStatusCode.Gone) return;
        var identifiers = ((BusinessContent)application.Content).Fields.GetProperty("identifiers").Deserialize<ApplicationIdentifier[]>(JsonSerializerOptions.Web)!;
        var replacement = applications.ReplaceDeleted(new(application.EntityId, identifiers));
        await RequestAsync("repair", new
        {
            action = rejected.VersionId == application.VersionId ? "replaceRejected" : "replaceReference",
            rejectedVersionId = rejected.VersionId.Value, entityId = replacement.Id.Value,
            content = ForegroundStore.ApplicationContent(replacement).Fields, schemaVersionId = registration.ApplicationSchema.Value,
            oldVersionId = application.VersionId.Value,
        }, token);
    }

    private async Task<VersionSubmission?> FindAsync(VersionId id, CancellationToken token)
    {
        var result = await RequestAsync("local", new { versionId = id.Value }, token);
        return result.GetProperty("versions").EnumerateArray().Select(value => VersionJson.ReadSubmission(value.GetProperty("version"))).SingleOrDefault();
    }
    private Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token) =>
        session.RequestAsync(method, JsonLines.Json(parameters), Guid.NewGuid().ToString(), token);
    internal async Task RunAsync(CancellationToken token)
    {
        while (true) { await SendAsync(token); await Task.Delay(TimeSpan.FromSeconds(2), token); }
    }
}
