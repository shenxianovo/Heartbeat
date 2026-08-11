using Heartbeat.Agent.Mac.Collectors;
using Heartbeat.Hub.Core.Ingest;

namespace Heartbeat.Agent.Mac.Tests.Collectors;

public class MacCollectorAppHintResolverTests
{
    [Theory]
    [InlineData("chrome", "mac:com.google.chrome")]
    [InlineData("edge", "mac:com.microsoft.edgemac")]
    [InlineData("brave", "mac:com.brave.browser")]
    [InlineData("opera", "mac:com.operasoftware.opera")]
    [InlineData("vivaldi", "mac:com.vivaldi.vivaldi")]
    [InlineData("firefox", "mac:org.mozilla.firefox")]
    public void Resolve_KnownBrowser_UsesBundleIdentity(string hint, string expected)
    {
        var resolution = new MacCollectorAppHintResolver().Resolve(hint);

        Assert.Equal(CollectorAppHintResolutionKind.Resolved, resolution.Kind);
        Assert.Equal(expected, resolution.AppIdentityKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Chrome")]
    [InlineData("safari")]
    [InlineData("browser")]
    public void Resolve_NonExactHint_DoesNotGuess(string hint)
    {
        var resolution = new MacCollectorAppHintResolver().Resolve(hint);

        Assert.Equal(CollectorAppHintResolutionKind.Unknown, resolution.Kind);
        Assert.Null(resolution.AppIdentityKey);
    }
}
