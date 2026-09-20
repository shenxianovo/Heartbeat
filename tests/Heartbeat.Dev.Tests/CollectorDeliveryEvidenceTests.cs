using Heartbeat.Dev;
using System.Text.Json;

namespace Heartbeat.Dev.Tests;

public sealed class CollectorDeliveryEvidenceTests
{
    private static readonly CollectorDeliveryEvidence Delivered = new(1, 1, 1, 4, 4, 1, 1);

    [Fact]
    public void RequiresEveryAcceptedRecordAndOneNativeApplicationObservation()
    {
        Delivered.RequireDelivered(4);

        CollectorDeliveryEvidence[] incomplete =
        [
            new(0, 0, 0, 0, 0, 0, 0), // Empty queue/database cannot prove a working Collector.
            Delivered with { Records = 3, MatchingRecords = 3 }, // A custody item disappeared.
            Delivered with { Records = 5, MatchingRecords = 5 }, // Duplicate delivery.
            Delivered with { MatchingCollectors = 0 }, // Wrong Owner, key or Target.
            Delivered with { MatchingRecords = 3 }, // Wrong identity, payload Target or collection time.
            Delivered with { Collectors = 2 }, // Registration split a stable Collector identity.
            Delivered with { Timelines = 2 },
            Delivered with { ApplicationRecords = 0, ValidApplicationRecords = 0 }, // Capability-only output.
            Delivered with { ValidApplicationRecords = 0 }, // Broken foreground protocol.
        ];
        foreach (var evidence in incomplete)
            Assert.Throws<InvalidOperationException>(() => evidence.RequireDelivered(4));
        Assert.Throws<InvalidOperationException>(() => Delivered.RequireDelivered(0));
    }

    [Fact]
    public void ExistingDataCannotStandInForThisCollectorsRegistration()
    {
        new CollectorDeliveryEvidence(0, 0, 0, 0, 0, 0, 0).RequireEmpty();

        Assert.Throws<InvalidOperationException>(() => Delivered.RequireEmpty());
        Assert.Throws<InvalidOperationException>(() => new CollectorDeliveryEvidence(1, 0, 0, 0, 0, 0, 0).RequireEmpty());
    }

    [Fact]
    public void EmptyOrPausedQueuesCannotProveSuccessfulCustody()
    {
        new HubQueueStatus(4, 0).RequireAccepted();

        Assert.Throws<InvalidOperationException>(() => new HubQueueStatus(0, 0).RequireAccepted());
        Assert.Throws<InvalidOperationException>(() => new HubQueueStatus(3, 1).RequireAccepted());
        Assert.False(new HubQueueStatus(1, 0).IsDrained());
        Assert.Throws<InvalidOperationException>(() => new HubQueueStatus(0, 1).IsDrained());
        Assert.True(new HubQueueStatus(0, 0).IsDrained());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pending\":0}")]
    [InlineData("{\"failed\":0}")]
    public void IncompleteStatusCannotBeMistakenForADrainedQueue(string json)
    {
        Assert.Throws<JsonException>(() => HubQueueStatus.Parse(json));
    }
}
