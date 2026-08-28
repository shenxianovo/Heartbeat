using System.Text.Json;
using Heartbeat.Collection.CollectorProtocol;

namespace Heartbeat.Collection.CollectorProtocol.Tests;

public sealed class CollectorProtocolClientTests
{
    [Fact]
    public async Task FactAcknowledgementCorpusDrivesDurableRemovalRetryAndDeadLetter()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Conformance",
            "collector-protocol-transcripts.json")));
        foreach (var item in corpus.RootElement.GetProperty("factAcknowledgements").EnumerateArray())
        {
            var status = Enum.Parse<CollectorFactDeliveryStatus>(
                item.GetProperty("status").GetString()!,
                ignoreCase: true);
            var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-client-{Guid.NewGuid():N}");
            try
            {
                var outcomes = status == CollectorFactDeliveryStatus.Retry
                    ? new Queue<CollectorFactDeliveryStatus>([status, CollectorFactDeliveryStatus.Committed])
                    : new Queue<CollectorFactDeliveryStatus>([status]);
                var binding = new FakeBinding(root, outcomes);
                await using var client = new CollectorProtocolClient(Definition(), binding);

                var result = await client.RunAsync(new PublishingApplication());

                var outbox = CollectorProtocolOutbox.Open(
                    root,
                    16,
                    Definition().Outputs,
                    DateTimeOffset.UtcNow);
                var eventuallyRemoved = item.GetProperty("removesFact").GetBoolean() ||
                    status == CollectorFactDeliveryStatus.Retry;
                Assert.Equal(eventuallyRemoved, outbox.Facts.Count == 0);
                Assert.Equal(
                    item.TryGetProperty("requiresDeadLetter", out var deadLetter) && deadLetter.GetBoolean() ? 1 : 0,
                    outbox.DeadLetterCount);
                Assert.Equal(outbox.Facts.Count, result.PendingFacts);
                if (status == CollectorFactDeliveryStatus.Retry)
                {
                    Assert.Equal(2, binding.PublishMessageIds.Count);
                    Assert.NotEqual(binding.PublishMessageIds[0], binding.PublishMessageIds[1]);
                    Assert.All(binding.PublishedFacts, fact =>
                    {
                        Assert.Equal(PublishingApplication.FactId, fact.FactId);
                        Assert.Equal(1, fact.Revision);
                    });
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CorruptOutboxIsQuarantinedAndBecomesOneGapPerDeclaredOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "collector-protocol-outbox.json");
        var recoveredAt = new DateTimeOffset(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);
        var lastWrite = recoveredAt.AddMinutes(-5);
        File.WriteAllText(path, "{truncated");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        try
        {
            var outbox = CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                recoveredAt);

            var gap = Assert.Single(outbox.Gaps).Gap;
            Assert.Equal("outbox_corrupted", gap.Reason);
            Assert.Equal(lastWrite, gap.Start);
            Assert.Equal(recoveredAt, gap.End);
            Assert.Single(Directory.EnumerateFiles(root, "collector-protocol-outbox.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransientPersistenceFailureRetriesWithoutAnotherObservation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var blockingDirectory = Path.Combine(root, "collector-protocol-outbox.json");
        Directory.CreateDirectory(blockingDirectory);
        try
        {
            var binding = new FakeBinding(
                root,
                new Queue<CollectorFactDeliveryStatus>([CollectorFactDeliveryStatus.Committed]));
            await using var client = new CollectorProtocolClient(Definition(), binding);
            var unblock = Task.Run(async () =>
            {
                await Task.Delay(120);
                Directory.Delete(blockingDirectory);
            });

            var result = await client.RunAsync(new PublishingApplication());
            await unblock;

            Assert.Empty(CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                DateTimeOffset.UtcNow).Facts);
            Assert.Equal(0, result.PendingFacts);
            Assert.Single(binding.PublishedFacts);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static CollectorClientDefinition Definition() => new(
        "reference.managed",
        new Dictionary<string, IReadOnlyList<int>>
        {
            ["facts.segment"] = [1],
            ["diagnostics.stream-gap"] = [1]
        },
        "account",
        [new CollectorOutputBinding("activity", "activity", new Dictionary<string, string>())]);

    private sealed class PublishingApplication : ICollectorProtocolApplication
    {
        public static readonly Guid FactId = Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999");

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            activation.PublishAsync(new CollectorFact(
                "activity",
                1,
                FactId,
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorSegmentFactTime(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), false),
                JsonSerializer.SerializeToElement(new { identityKey = "reference|online", title = "Online" })),
                cancellationToken);

        public ValueTask StopAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FakeBinding(
        string dataDirectory,
        Queue<CollectorFactDeliveryStatus> outcomes) : ICollectorProtocolBinding
    {
        public List<Guid> PublishMessageIds { get; } = [];
        public List<BoundCollectorFact> PublishedFacts { get; } = [];

        public ValueTask<CollectorClientInitialization> StartAsync(
            CollectorClientDefinition definition,
            CancellationToken cancellationToken) => ValueTask.FromResult(new CollectorClientInitialization(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "account",
                1,
                1,
                JsonSerializer.SerializeToElement(new { }),
                500,
                1_048_576,
                dataDirectory,
                definition.Capabilities.ToDictionary(pair => pair.Key, _ => 1)));

        public ValueTask<IReadOnlyDictionary<string, CollectorClientStream>> OpenStreamsAsync(
            long specRevision,
            IReadOnlyList<CollectorOutputBinding> outputs,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<string, CollectorClientStream>>(
            new Dictionary<string, CollectorClientStream>
            {
                ["activity"] = new(
                    "activity", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "account",
                    "activity", "reference.account", "segment", "heartbeat.reference.segment", 1, 1,
                    "sha256:test", new Dictionary<string, string>())
            });

        public ValueTask ReadyAsync(long appliedSpecRevision, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<CollectorFactBatchAcknowledgement> PublishAsync(
            Guid messageId,
            IReadOnlyList<BoundCollectorFact> facts,
            CancellationToken cancellationToken)
        {
            PublishMessageIds.Add(messageId);
            PublishedFacts.AddRange(facts);
            var status = outcomes.Dequeue();
            var error = status is CollectorFactDeliveryStatus.Rejected or CollectorFactDeliveryStatus.Retry
                ? new CollectorProtocolError("fixture", "Fixture outcome.", status == CollectorFactDeliveryStatus.Retry)
                : null;
            return ValueTask.FromResult(new CollectorFactBatchAcknowledgement(
                [new CollectorFactDeliveryOutcome(
                    0,
                    status,
                    error,
                    status == CollectorFactDeliveryStatus.Retry ? 1 : null)]));
        }

        public ValueTask<CollectorGapDeliveryOutcome> ReportGapAsync(
            Guid messageId,
            Guid streamId,
            CollectorStreamGap gap,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new CollectorGapDeliveryOutcome(CollectorGapDeliveryStatus.Committed));

        public ValueTask<CollectorAuthorizationResponse> ChallengeAsync(
            Guid interactionId,
            string kind,
            string title,
            string? message,
            IReadOnlyList<CollectorAuthorizationField> fields,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CompleteAuthorizationAsync(Guid interactionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string?> ReadSecretAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask WriteSecretAsync(string key, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DeleteSecretAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CollectorDrainRequest> WaitForDrainAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CollectorDrainRequest(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddSeconds(5)));

        public ValueTask CompleteDrainAsync(CollectorDrainResult result, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
