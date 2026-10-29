using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed class IdentityRecognizer(IdentityApi api, IdentityState state, IApplicationModel model,
    string modelName, string programVersion, Action<string> diagnostic)
{
    public async Task RunOnceAsync(CancellationToken token)
    {
        if (!model.IsConfigured) { diagnostic("OPENAI_API_KEY 未配置，本轮不识别。"); return; }
        try
        {
            await api.ResumeAsync(token);
            var productSchema = await api.EnsureAsync(IdentityData.ProductSchemaId, null, IdentityData.ProductSchema, token);
            var observerSchema = await api.EnsureAsync(IdentityData.ObserverSchemaId, null, IdentityData.ObserverSchema, token);
            var observer = await api.EnsureAsync(state.ObserverId, observerSchema.VersionId,
                IdentityData.Content(new { programVersion, model = "openai/" + modelName }), token);
            var applications = await api.ListAsync("local-applications", token);
            var groups = applications.SelectMany(app => IdentityData.Identifiers(app).Where(i => i.IsShared).Distinct()
                .Select(identifier => (identifier, app))).GroupBy(item => item.identifier);
            foreach (var group in groups)
            {
                // Refresh the catalogue after each write, including concurrent manual changes.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await IdentifyAsync(group.Key, group.Select(item => item.app).ToArray(), productSchema.VersionId, observer.VersionId, token);
                        break;
                    }
                    catch (IdentityRejection exception) when (exception.Rule == "VERSION-6" && attempt == 0)
                    { diagnostic("产品已更新，重新读取并判断。"); }
                    catch (IdentityRejection exception) { diagnostic($"提交被拒绝：{exception.Rule} {exception.Message}"); break; }
                }
            }
        }
        catch (HttpRequestException exception) { diagnostic($"API 暂时不可用：{exception.Message}"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { diagnostic("API 请求超时，下轮重试。"); }
        catch (IdentityRejection exception) { diagnostic($"提交被拒绝：{exception.Rule} {exception.Message}"); }
    }

    private async Task IdentifyAsync(ApplicationIdentifier identifier, EntityVersion[] apps,
        VersionId productSchema, VersionId observer, CancellationToken token)
    {
        var products = (await api.ListAsync("application-products", token)).Select(IdentityData.Product).ToArray();
        var key = JsonSerializer.Serialize(identifier, IdentityData.Json);
        if (products.Any(product => product.Identifiers.Contains(identifier)))
        {
            if (state.Unknown.Remove(key)) state.SaveUnknown();
            return;
        }
        var periods = new List<EntityVersion>();
        foreach (var app in apps) periods.AddRange(await api.PeriodsAsync(app.EntityId, token));
        var names = periods.Select(period => ((BusinessContent)period.Content).Fields.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = JsonSerializer.Serialize(new { applications = apps.Select(app => app.EntityId.Value).Order().ToArray(), names });
        if (state.Unknown.GetValueOrDefault(key) == fingerprint) return;
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
            state.Unknown[key] = fingerprint;
            state.SaveUnknown();
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
        await api.SubmitAsync([inputs, product, observation], token);
        state.Unknown.Remove(key);
        state.SaveUnknown();
    }
}
