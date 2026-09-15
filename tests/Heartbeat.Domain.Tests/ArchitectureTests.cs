namespace Heartbeat.Domain.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void DomainDoesNotDependOnEntityFramework()
    {
        var references = typeof(Heartbeat.Recording.Record).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference =>
            reference.Name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true);
    }
}
