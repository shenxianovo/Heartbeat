using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRecognizer(DataClient data, IdentityRepository repository, IApplicationModel model,
    string modelName, string programVersion, Action<string> diagnostic)
{
    // Identifiers the model answered unknown for, with what it saw then. Kept only while the process runs.
    private readonly Dictionary<string, string> unknown = [];

    public async Task RunOnceAsync(CancellationToken token)
    {
        await data.UseAsync(IdentityData.Definitions, IdentityData.ObserverSchemaId.Value,
            new { programVersion, model = "openai/" + modelName }, token);
        if (!model.IsConfigured) { diagnostic("OPENAI_API_KEY 未配置，本轮不识别。"); return; }
        var applications = await repository.ApplicationsAsync(token);
        var groups = applications.SelectMany(app => IdentityData.Identifiers(app).Where(i => i.IsShared).Distinct()
            .Select(identifier => (identifier, app))).GroupBy(item => item.identifier);
        // Each decision sees the products and memberships saved by the previous decision.
        foreach (var group in groups)
        {
            try { await IdentifyAsync(group.Key, group.Select(item => item.app).ToArray(), token); }
            catch (Exception exception) when (exception is WriteRejectedException or HttpRequestException) { diagnostic($"这条线索下轮重试：{exception.Message}"); }
        }
    }

    private async Task IdentifyAsync(ApplicationIdentifier identifier, EntityVersion[] apps, CancellationToken token)
    {
        var (products, memberships) = await repository.CatalogAsync(token);
        var key = JsonSerializer.Serialize(identifier, IdentityData.Json);
        if (memberships.Any(m => m.Identifier == identifier && m.ProductId is not null))
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
                products.Select(product => new ProductCandidate(product.Version.EntityId.Value, product.Name, memberships.Where(m => m.ProductId == product.Version.EntityId).Select(m => m.Identifier).ToArray())).ToArray()), token);
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
        var inputs = apps.Concat(periods.Where(p => !string.IsNullOrWhiteSpace(((BusinessContent)p.Content).Fields.GetProperty("name").GetString())))
            .Concat(products.Select(product => product.Version)).Concat(memberships.Select(m => m.Version))
            .Select(v => v.VersionId.Value).Distinct().ToArray();
        var productId = target?.Version.EntityId.Value;
        if (productId is null)
        {
            var created = await data.WriteAsync(new(IdentityData.ProductSchemaId.Value, new ProductContent(decision.Name!)), token);
            productId = created!.Version!.Value.GetProperty("entityId").GetGuid();
        }
        await data.WriteAsync(new(IdentityData.MembershipSchemaId, new MembershipContent(identifier, Ref.Entity(productId.Value)),
            new(null, identifier.Scheme, identifier.Value), Time: FactTime.Unknown(), Inputs: inputs), token);
        unknown.Remove(key);
    }
}
