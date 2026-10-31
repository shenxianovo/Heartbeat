using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRecognizer(IdentityRepository repository, IdentityWork work, IApplicationModel model,
    string modelName, string programVersion, Action<string> diagnostic)
{
    // Identifiers the model answered unknown for, with what it saw then. Kept only while the process runs.
    private readonly Dictionary<string, string> unknown = [];

    public async Task RunOnceAsync(CancellationToken token)
    {
        if (!model.IsConfigured) { diagnostic("OPENAI_API_KEY 未配置，本轮不识别。"); return; }
        try
        {
            var productSchema = await repository.EnsureAsync(IdentityData.ProductSchemaId, null, IdentityData.ProductSchema, token);
            var observerSchema = await repository.EnsureAsync(IdentityData.ObserverSchemaId, null, IdentityData.ObserverSchema, token);
            var observer = await repository.EnsureAsync(work.InstanceId, observerSchema.VersionId,
                IdentityData.Content(new { programVersion, model = "openai/" + modelName }), token);
            var applications = await repository.ListAsync("local-applications", token);
            var groups = applications.SelectMany(app => IdentityData.Identifiers(app).Where(i => i.IsShared).Distinct()
                .Select(identifier => (identifier, app))).GroupBy(item => item.identifier);
            // The catalogue is read again for each identifier, so it includes the previous write and manual changes.
            foreach (var group in groups)
            {
                try { await IdentifyAsync(group.Key, group.Select(item => item.app).ToArray(), productSchema.VersionId, observer.VersionId, token); }
                catch (SubmissionRejectedException exception) { diagnostic($"提交被拒绝：{exception.Rule} {exception.Message}"); }
            }
        }
        catch (SubmissionRejectedException exception) { diagnostic($"提交被拒绝：{exception.Rule} {exception.Message}"); }
    }

    private async Task IdentifyAsync(ApplicationIdentifier identifier, EntityVersion[] apps,
        VersionId productSchema, VersionId observer, CancellationToken token)
    {
        var products = (await repository.ListAsync("application-products", token)).Select(IdentityData.Product).ToArray();
        var key = JsonSerializer.Serialize(identifier, IdentityData.Json);
        if (products.Any(product => product.Identifiers.Contains(identifier)))
        {
            unknown.Remove(key);
            return;
        }
        var periods = new List<EntityVersion>();
        foreach (var app in apps) periods.AddRange(await repository.PeriodsAsync(app.EntityId, token));
        var names = periods.Select(period => ((BusinessContent)period.Content).Fields.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = JsonSerializer.Serialize(new { applications = apps.Select(app => app.EntityId.Value).Order().ToArray(), names });
        if (unknown.GetValueOrDefault(key) == fingerprint) return;
        IdentificationDecision decision;
        try
        {
            decision = await model.IdentifyAsync(new(identifier.Scheme.Split('.')[0], identifier,
                apps.Select(IdentityData.Identifiers).ToArray(), names,
                products.Select(product => new ProductCandidate(product.Version.EntityId.Value, product.Name, product.Identifiers)).ToArray()), token);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
            || exception is OperationCanceledException && !token.IsCancellationRequested)
        { diagnostic($"模型判断失败，下轮重试：{exception.Message}"); return; }
        if (decision.Kind == "unknown" && decision.ProductId is null && decision.Name is null)
        {
            unknown[key] = fingerprint;
            return;
        }
        var target = decision.Kind == "existing" ? products.SingleOrDefault(product => product.Version.EntityId.Value == decision.ProductId) : null;
        if (decision.Kind is not ("existing" or "new") || decision.Kind == "existing" && target is null
            || decision.Kind == "existing" && decision.Name is not null
            || decision.Kind == "new" && (string.IsNullOrWhiteSpace(decision.Name) || decision.ProductId is not null))
        { diagnostic("模型回答无效，下轮重试。"); return; }
        var inputs = new VersionSubmission(EntityId.New(), VersionId.New(), null, null,
            new InputSet(apps.Concat(periods).Concat(products.Select(product => product.Version)).Select(v => v.VersionId).Distinct()));
        var product = new VersionSubmission(target?.Version.EntityId ?? EntityId.New(), VersionId.New(), target?.Version.VersionId,
            productSchema, IdentityData.Content(new { name = target?.Name ?? decision.Name!,
                identifiers = (target?.Identifiers ?? []).Append(identifier).Distinct().ToArray() }));
        var observation = new VersionSubmission(EntityId.New(), VersionId.New(), null, null,
            new Observation(observer, product.VersionId, inputs.VersionId, null, null, null));
        await repository.SubmitAsync([inputs, product, observation], token);
        unknown.Remove(key);
    }
}
