using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-artifacts-{Guid.NewGuid():N}");

    [Fact]
    public void PruningKeepsNewestRunsAndRequiresCallerToDelete()
    {
        var store = new ArtifactStore(new RepositoryContext(_root));
        Directory.CreateDirectory(store.Root);
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(store.Root, $"run-{index}");
            Directory.CreateDirectory(path);
            Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-30).AddMinutes(index));
        }

        var candidates = store.SelectForPruning(keep: 1, TimeSpan.FromDays(14));

        Assert.Equal(2, candidates.Count);
        Assert.Equal(3, Directory.GetDirectories(store.Root).Length);
        foreach (var candidate in candidates) store.Delete(candidate);
        Assert.Single(Directory.GetDirectories(store.Root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
