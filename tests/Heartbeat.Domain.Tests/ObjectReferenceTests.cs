using Heartbeat.Contracts;

namespace Heartbeat.Domain.Tests;

public sealed class ObjectReferenceTests
{
    [Fact]
    public void ScopedIdentificationRequiresItsObservedScopeAndOrdersItFirst()
    {
        ObjectReference application = new("application", "app.bundle", "com.example", Scope: new("device", "mac"));
        Assert.Throws<ArgumentException>(() => ObjectReference.Normalize([application]));
        var normalized = ObjectReference.Normalize([application, new("device", "device", "mac")]);
        Assert.Null(normalized[0].Scope);
        Assert.Equal(application, normalized[1]);
    }

    [Fact]
    public void KnownIdentityCanHaveSeveralAliasesButRequiresCompleteIdentification()
    {
        var id = Guid.CreateVersion7();
        var normalized = ObjectReference.Normalize([new("source", Id: id),
            new("source", "address", "second", Id: id), new("source", "address", "first", Id: id)]);
        Assert.Equal(3, normalized.Length);
        Assert.All(normalized, item => Assert.Equal(id, item.Id));
        Assert.Throws<ArgumentException>(() => ObjectReference.Normalize([new("source", Namespace: "address", Id: id)]));
        Assert.Throws<ArgumentException>(() => ObjectReference.Normalize([new("source", Id: Guid.NewGuid())]));
        Assert.Throws<ArgumentException>(() => ObjectReference.Normalize([new("source", Id: id, Scope: new("device", "mac"))]));
        Assert.Throws<ArgumentException>(() => ObjectReference.Normalize([new("source")]));
    }
}
