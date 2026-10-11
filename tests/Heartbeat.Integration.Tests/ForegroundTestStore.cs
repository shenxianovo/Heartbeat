using Heartbeat.Core;
using Heartbeat.Observers.ForegroundState;

namespace Heartbeat.Integration.Tests;

internal sealed class ForegroundTestStore : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("heartbeat-outbox-");
    internal string Path => System.IO.Path.Combine(_directory.FullName, "state.sqlite");
    internal ForegroundStore Store { get; }
    internal ForegroundTestStore(EntityId observerId) => Store = new ForegroundStore(Path, observerId);
    public void Dispose() => _directory.Delete(recursive: true);
}
