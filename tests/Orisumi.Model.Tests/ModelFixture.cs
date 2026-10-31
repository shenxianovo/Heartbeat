using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Model.Tests;

internal sealed class ModelFixture : IDisposable
{
    internal InMemoryEntityStore Store { get; } = new();
    internal Clock Time { get; } = new();
    internal EntityService Service { get; }
    internal EntityId ObserverId { get; } = EntityId.New();
    internal ModelFixture()
    {
        Service = new(Store, Time);
        Store.ExecuteAsync(async (transaction, token) => { await transaction.CreateThingAsync(ObserverId, BuiltInSchemas.Program, token); return true; }).GetAwaiter().GetResult();
    }
    internal Observation Content(JsonElement fields) => new(ObserverId, null, "direct", fields.Clone());
    internal static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    internal static VersionSubmission New(VersionContent content, VersionId? schema = null) =>
        new(EntityId.New(), VersionId.New(), null, schema, content);
    internal static VersionSubmission Next(EntityVersion previous, VersionContent? content = null) =>
        previous.Submission with { VersionId = VersionId.New(), PreviousVersionId = previous.VersionId, Content = content ?? previous.Content };
    internal Task<EntityVersion> Save(VersionSubmission version) => Store.ExecuteAsync((transaction, token) => Service.SaveAsync(transaction, VersionTime.Normalize(version), token));
    internal Task<IReadOnlyList<EntityVersion>> SaveMany(params VersionSubmission[] versions) => Store.ExecuteAsync<IReadOnlyList<EntityVersion>>(async (transaction, token) =>
    {
        var saved = new List<EntityVersion>();
        foreach (var version in versions) saved.Add(await Service.SaveAsync(transaction, VersionTime.Normalize(version), token));
        return saved;
    });
    internal Task<EntityVersion> Schema(string resource = "items", string fields = "{}") =>
        Save(New(new EntitySchema("Items", "An item", resource, Json(fields), "observation", false)));
    internal Task<EntityVersion> Observe(EntityVersion schema, string content = "{}") =>
        Save(New(Content(Json(content)), schema.VersionId));
    internal static async Task Reject(Func<Task> action, string rule)
    {
        try { await action(); }
        catch (ModelViolationException exception)
        {
            await Assert.That(exception.Rule).IsEqualTo(rule);
            return;
        }
        throw new InvalidOperationException($"Expected rejection under {rule}.");
    }
    public void Dispose() => Store.Dispose();

    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
