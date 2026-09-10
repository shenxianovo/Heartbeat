using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Desktop.UI.Tests.Hosting;

public sealed class DesktopDevelopmentTests
{
    [Fact]
    public void DevelopmentBinding_RequiresOwnedIndependentProfileAndSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "heartbeat-development-" + Guid.NewGuid().ToString("N"));
        try
        {
            var daily = Path.Combine(root, "daily");
            var development = Path.Combine(root, "dev");
            Assert.Throws<ArgumentException>(() => new DesktopBootstrap(["--development"], daily));
            using (var production = new DesktopBootstrap(["--development", "--data-directory", daily], daily))
            {
                Assert.True(production.TryAcquire("heartbeat-test-" + Guid.NewGuid()));
                Assert.Throws<InvalidOperationException>(() => production.PrepareDevelopmentBinding());
            }
            ExternalHostProfileBinding first;
            using (var bootstrap = new DesktopBootstrap(["--development", "--data-directory", development], daily))
            {
                Assert.Throws<InvalidOperationException>(() => bootstrap.PrepareDevelopmentBinding());
                Assert.True(bootstrap.TryAcquire("unused"));
                first = bootstrap.PrepareDevelopmentBinding()!;
            }
            using (var bootstrap = new DesktopBootstrap(["--development", "--data-directory", development], daily))
            {
                Assert.True(bootstrap.TryAcquire("unused"));
                Assert.Equal(first, bootstrap.PrepareDevelopmentBinding());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
