using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Model.Tests;

internal sealed class ModelFixture : IDisposable
{
    internal InMemoryEntityStore Store { get; } = new();
    internal Clock Time { get; } = new();
    internal EntityService Service { get; }
    internal ModelFixture() => Service = new(Store, Time);
    internal static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    internal static VersionSubmission New(VersionContent content, VersionId? schema = null) =>
        new(EntityId.New(), VersionId.New(), null, schema, content);
    internal static VersionSubmission Next(EntityVersion previous, VersionContent? content = null) =>
        previous.Submission with { VersionId = VersionId.New(), PreviousVersionId = previous.VersionId, Content = content ?? previous.Content };
    internal async Task<EntityVersion> Save(VersionSubmission version) => (await Service.SubmitAsync([version])).RequireSuccess()[0];
    internal Task<EntityVersion> Schema(string resource = "items", string fields = "{}") =>
        Save(New(new EntitySchema("Items", "An item", resource, Json(fields))));
    internal Task<EntityVersion> Business(EntityVersion schema, string content = "{}") =>
        Save(New(new BusinessContent(Json(content)), schema.VersionId));
    internal static async Task Reject(Func<Task<SubmissionResult>> action, string rule)
    {
        var result = await action();
        await Assert.That(result.Rejection?.Rule).IsEqualTo(rule);
    }
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
