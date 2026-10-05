using Heartbeat.Recording;

namespace Heartbeat.Domain.Tests;

public sealed class RecordingObjectTests
{
    [Fact]
    public void CreationAllocatesIdentityAndCollectorUsesIt()
    {
        var ownerId = Guid.NewGuid();
        var identity = RecordingObject.Create(ownerId);
        var another = RecordingObject.Create(ownerId);
        var collector = Collector.Create(Guid.NewGuid(), identity,
            "example.collector", "target", "Name", DateTimeOffset.UtcNow);

        Assert.Equal(7, identity.Id.Version);
        Assert.Equal(ownerId, identity.OwnerId);
        Assert.NotEqual(identity.Id, another.Id);
        Assert.Equal(identity.Id, collector.Id);
        Assert.Same(identity, collector.Identity);
    }

    [Fact]
    public void CreationRequiresOwner()
    {
        Assert.Throws<ArgumentException>(() => RecordingObject.Create(Guid.Empty));
    }

    [Fact]
    public void RegistrationPreservesProducerIdentityAndRejectsInvalidIdentityOrOwner()
    {
        var id = Guid.CreateVersion7();
        var owner = Guid.NewGuid();
        Assert.Equal(id, RecordingObject.Register(id, owner).Id);
        Assert.Throws<ArgumentException>(() => RecordingObject.Register(Guid.NewGuid(), owner));
        Assert.Throws<ArgumentException>(() => RecordingObject.Register(id, Guid.Empty));
    }
}
