using Heartbeat.Agent.Collectors;
using Heartbeat.Hub.Core.Ingest;

namespace Heartbeat.Agent.Tests.Collectors;

public class WindowsCollectorAppHintResolverTests
{
    [Theory]
    [InlineData("chrome", "win:chrome")]
    [InlineData("edge", "win:msedge")]
    [InlineData("brave", "win:brave")]
    [InlineData("opera", "win:opera")]
    [InlineData("vivaldi", "win:vivaldi")]
    [InlineData("firefox", "win:firefox")]
    public void Resolve_KnownBrowser_UsesProcessIdentity(string hint, string expected)
    {
        var resolution = new WindowsCollectorAppHintResolver().Resolve(hint);

        Assert.Equal(CollectorAppHintResolutionKind.Resolved, resolution.Kind);
        Assert.Equal(expected, resolution.AppIdentityKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Chrome")]
    [InlineData("msedge")]
    [InlineData("browser")]
    public void Resolve_NonExactHint_DoesNotGuess(string hint)
    {
        var resolution = new WindowsCollectorAppHintResolver().Resolve(hint);

        Assert.Equal(CollectorAppHintResolutionKind.Unknown, resolution.Kind);
        Assert.Null(resolution.AppIdentityKey);
    }
}
