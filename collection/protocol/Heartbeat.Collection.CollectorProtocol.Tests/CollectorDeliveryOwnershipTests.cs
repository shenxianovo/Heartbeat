using Heartbeat.Collection.CollectorProtocol;

namespace Heartbeat.Collection.CollectorProtocol.Tests;

public sealed class CollectorDeliveryOwnershipTests
{
    [Fact]
    public void BeginDrainAtomicallyTransfersDeliveryOwnershipAndCapturesAbsoluteDeadline()
    {
        var deadline = new DateTimeOffset(2026, 8, 31, 12, 34, 56, TimeSpan.Zero);
        var ownership = new CollectorDeliveryOwnership();
        var background = ownership.BeginBackground();

        var ordinaryAdmission = ownership.BeginOrdinaryAdmission();
        var drain = ownership.BeginDrain(new CollectorDrainRequest(Guid.CreateVersion7(), deadline));

        Assert.Equal(deadline, drain.Deadline);
        Assert.Throws<CollectorAdmissionClosedException>(ownership.BeginOrdinaryAdmission);
        _ = drain.BeginTailAdmission();
        Assert.Equal(
            CollectorAdmissionOutcome.Superseded,
            ordinaryAdmission.TryCommit(() => throw new InvalidOperationException("stale admission ran")));
        Assert.Equal(
            CollectorDeliveryCommitOutcome.Superseded,
            background.TryCommit(() => throw new InvalidOperationException("stale commit ran")));

        var committed = false;
        Assert.Equal(
            CollectorDeliveryCommitOutcome.Committed,
            drain.Delivery.TryCommit(() => committed = true));
        Assert.True(committed);
    }

    [Theory]
    [InlineData(PendingDeliveryKind.Fact)]
    [InlineData(PendingDeliveryKind.Gap)]
    public void SupersededFactAndGapCommitRemainDurableUntilDrainLeaseConverges(
        PendingDeliveryKind kind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-delivery-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 8, 31, 13, 0, 0, TimeSpan.Zero);
        try
        {
            var outbox = CollectorProtocolOutbox.Open(
                root,
                16,
                [new CollectorOutputBinding("activity", "activity", new Dictionary<string, string>())],
                now);
            var ownership = new CollectorDeliveryOwnership();
            var background = ownership.BeginBackground();
            if (kind == PendingDeliveryKind.Fact)
            {
                outbox.Enqueue(new CollectorFact(
                    "activity",
                    1,
                    Guid.CreateVersion7(),
                    1,
                    now,
                    CollectorFactRecordState.Present,
                    new CollectorEventFactTime(now),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { identityKey = "owner|fact" })));
            }
            else
            {
                outbox.EnqueueGap(new CollectorStreamGap(
                    Guid.CreateVersion7(),
                    "activity",
                    now,
                    now.AddSeconds(1),
                    "fixture",
                    1));
            }
            var drain = ownership.BeginDrain(new CollectorDrainRequest(Guid.CreateVersion7(), now.AddMinutes(1)));

            var stale = kind == PendingDeliveryKind.Fact
                ? outbox.AcknowledgeFact(outbox.FirstFact!.MessageId, background)
                : outbox.AcknowledgeGap(outbox.FirstGap!.MessageId, background);

            Assert.Equal(CollectorDeliveryCommitOutcome.Superseded, stale);
            Assert.Equal(1, kind == PendingDeliveryKind.Fact ? outbox.Facts.Count : outbox.Gaps.Count);

            var converged = kind == PendingDeliveryKind.Fact
                ? outbox.AcknowledgeFact(outbox.FirstFact!.MessageId, drain.Delivery)
                : outbox.AcknowledgeGap(outbox.FirstGap!.MessageId, drain.Delivery);

            Assert.Equal(CollectorDeliveryCommitOutcome.Committed, converged);
            Assert.Empty(kind == PendingDeliveryKind.Fact ? outbox.Facts : outbox.Gaps);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeadlineFenceRejectsLateDrainAdmissionAndDurableCommit()
    {
        var ownership = new CollectorDeliveryOwnership();
        ownership.BeginBackground();
        var drain = ownership.BeginDrain(new CollectorDrainRequest(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 31, 14, 0, 0, TimeSpan.Zero)));
        drain.Fence();

        var lateCommitRan = false;
        Assert.Equal(
            CollectorDeliveryCommitOutcome.Fenced,
            drain.Delivery.TryCommit(() => lateCommitRan = true));
        Assert.False(lateCommitRan);
        Assert.Throws<CollectorAdmissionClosedException>(drain.BeginTailAdmission);
    }

    [Fact]
    public void OrdinaryAdmissionPreparedBeforeDrainCannotPublishAfterOwnershipTransfer()
    {
        var ownership = new CollectorDeliveryOwnership();
        ownership.BeginBackground();
        var admission = ownership.BeginOrdinaryAdmission();
        var preparedMutation = true;

        ownership.BeginDrain(new CollectorDrainRequest(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 31, 15, 0, 0, TimeSpan.Zero)));
        var authoritativeMutationRan = false;
        var outcome = admission.TryCommit(() => authoritativeMutationRan = preparedMutation);

        Assert.Equal(CollectorAdmissionOutcome.Superseded, outcome);
        Assert.False(authoritativeMutationRan);
    }

    [Fact]
    public void DirtyGapRetryWithSameAdmissionPersistsBeforeReportingCommitted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-gap-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 8, 31, 16, 0, 0, TimeSpan.Zero);
        var firstPublish = true;
        try
        {
            var outbox = CollectorProtocolOutbox.Open(
                root,
                16,
                [new CollectorOutputBinding("activity", "activity", new Dictionary<string, string>())],
                now,
                (prepared, authoritative) =>
                {
                    if (firstPublish)
                    {
                        firstPublish = false;
                        throw new IOException("fixture persistence failure");
                    }
                    File.Move(prepared, authoritative, overwrite: true);
                    return true;
                });
            var gap = new CollectorStreamGap(
                Guid.CreateVersion7(),
                "activity",
                now,
                now.AddSeconds(1),
                "fixture",
                1);
            var admission = new CollectorDeliveryOwnership().BeginOrdinaryAdmission();
            Assert.Throws<IOException>(() => outbox.EnqueueGap(gap, admission));

            Assert.Equal(
                CollectorAdmissionOutcome.Committed,
                outbox.EnqueueGap(gap, admission));

            var restarted = CollectorProtocolOutbox.Open(
                root,
                16,
                [new CollectorOutputBinding("activity", "activity", new Dictionary<string, string>())],
                now.AddMinutes(1));
            Assert.Equal(gap.GapId, Assert.Single(restarted.Gaps).Gap.GapId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeadlineFirstRemainsTheTerminalCauseWhenCallerCancelsLater()
    {
        var ownership = new CollectorDeliveryOwnership();
        ownership.BeginBackground();
        var drain = ownership.BeginDrain(new CollectorDrainRequest(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 31, 17, 0, 0, TimeSpan.Zero)));
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        using var cancellation = drain.BeginCancellation(caller.Token, deadline.Token);

        deadline.Cancel();
        caller.Cancel();

        Assert.Equal(CollectorDrainCancellationCause.Deadline, cancellation.FirstCause);
        Assert.True(cancellation.Token.IsCancellationRequested);
        Assert.Throws<CollectorAdmissionClosedException>(drain.BeginTailAdmission);
    }

    [Fact]
    public void CallerFirstRemainsTheTerminalCauseWhenDeadlineCancelsLater()
    {
        var ownership = new CollectorDeliveryOwnership();
        ownership.BeginBackground();
        var drain = ownership.BeginDrain(new CollectorDrainRequest(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 31, 18, 0, 0, TimeSpan.Zero)));
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        using var cancellation = drain.BeginCancellation(caller.Token, deadline.Token);

        caller.Cancel();
        deadline.Cancel();

        Assert.Equal(CollectorDrainCancellationCause.Caller, cancellation.FirstCause);
        Assert.True(cancellation.Token.IsCancellationRequested);
        Assert.Throws<CollectorAdmissionClosedException>(drain.BeginTailAdmission);
    }

    public enum PendingDeliveryKind
    {
        Fact,
        Gap
    }
}
