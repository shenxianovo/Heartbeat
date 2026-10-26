using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class SourceMetricsTests
{
    [Test]
    [Arguments("src/Docs/Orisumi.Docs/content/docs/core/index.mdx", "Markdown", "Documentation")]
    [Arguments("src/Docs/Orisumi.Docs/pnpm-lock.yaml", "YAML", "Generated")]
    [Arguments("src/Docs/Orisumi.Docs/package.json", "JSON", "Build")]
    [Arguments("src/Docs/Orisumi.Docs/content/docs/meta.json", "JSON", "Documentation")]
    [Arguments("src/Docs/Orisumi.Docs/content/docs/api/openapi.json", "JSON", "Documentation")]
    [Arguments("src/Docs/Orisumi.Docs/lib/source.ts", "TypeScript", "Production")]
    public async Task CurrentDocsFilesKeepTheirIntendedRole(string path, string language, string expected)
    {
        await Assert.That(SourceCorpus.TryClassify(path, out var actualLanguage, out var role)).IsTrue();
        await Assert.That(actualLanguage).IsEqualTo(language);
        await Assert.That(role.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task SharedTestInfrastructureIsCountedWithBackendTests()
    {
        var report = LocReport.Create(new SourceSnapshot("worktree",
            [new("tests/Orisumi.Testing/ApiFixture.cs", "C#", SourceRole.Test, 12)]));
        await Assert.That(report.UnassignedTestPaths.Count).IsEqualTo(0);
        await Assert.That(report.Modules.Single().Module).IsEqualTo("Backend");
        await Assert.That(report.Modules.Single().Tests).IsEqualTo(12);
    }
}
