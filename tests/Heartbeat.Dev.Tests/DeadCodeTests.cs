using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DeadCodeTests
{
    [Test]
    public async Task KnipReportsUnusedFilesAndPreservesDocsLocations()
    {
        const string json = """
            {"issues":[{"file":"lib/unused.ts","files":[{}]},{"file":"lib/source.ts","exports":[{"name":"unused","line":7}],"dependencies":[{"name":"unused-package"}]}]}
            """;
        var findings = DeadCodeDetector.ParseKnip(json);
        await Assert.That(findings.Count).IsEqualTo(3);
        await Assert.That(findings.Any(item => item.Path == "src/Docs/Heartbeat.Docs/lib/unused.ts"
            && item.Rule == "knip/files")).IsTrue();
        await Assert.That(findings.Any(item => item.Path == "src/Docs/Heartbeat.Docs/lib/source.ts"
            && item.Line == 7 && item.Symbol == "unused")).IsTrue();
    }

    [Test]
    public async Task UnusedDeveloperToolMembersAreIncluded()
    {
        var root = Path.GetTempPath();
        var path = Path.Combine(root, "tools", "Heartbeat.Dev", "Example.cs");
        var findings = DeadCodeDetector.ParseCSharp(root,
            $"{path}(9,2): warning IDE0051: Private member 'Example.Unused' is unused");
        await Assert.That(findings.Count).IsEqualTo(1);
    }
}
