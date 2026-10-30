using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class LocReportTests
{
    [Test]
    [Arguments("src/Clients/Orisumi.Mac/Orisumi/ContentView.swift", "Swift", "Production")]
    [Arguments("src/Clients/Orisumi.Mac/OrisumiClient/Tests/OrisumiClientTests/ClientTests.swift", "Swift", "Test")]
    [Arguments("tests/Orisumi.Model.Tests/Orisumi.Model.Tests.csproj", "MSBuild", "Build")]
    [Arguments("tests/Orisumi.Model.Tests/README.md", "Markdown", "Documentation")]
    public async Task FilesAreClassifiedByLanguageBeforeTheirTestLocation(
        string path, string expectedLanguage, string expectedRole)
    {
        await Assert.That(SourceCorpus.TryClassify(path, out var language, out var role)).IsTrue();
        await Assert.That(language).IsEqualTo(expectedLanguage);
        await Assert.That(role.ToString()).IsEqualTo(expectedRole);
    }

    [Test]
    public async Task TestProjectsBelongToTheOnlyModuleTheyReference()
    {
        var snapshot = new SourceSnapshot("worktree",
        [
            new("src/Core/Orisumi.Core/Observation.cs", "C#", SourceRole.Production, 40),
            new("src/Backend/Orisumi.Api/Program.cs", "C#", SourceRole.Production, 60),
            new("src/Clients/Orisumi.Web/src/app.test.ts", "TypeScript", SourceRole.Test, 7),
            new("tests/Orisumi.Model.Tests/ModelTests.cs", "C#", SourceRole.Test, 20),
            new("tests/Orisumi.Integration.Tests/ApiTests.cs", "C#", SourceRole.Test, 30),
            new("tests/Orisumi.Integration.Tests/Orisumi.Integration.Tests.csproj", "MSBuild", SourceRole.Build, 9),
            new("pnpm-lock.yaml", "YAML", SourceRole.Generated, 500),
        ]);
        var references = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Orisumi.Model.Tests"] = ["src/Core/Orisumi.Core/Orisumi.Core.csproj", "tests/Orisumi.Testing/Orisumi.Testing.csproj"],
            ["Orisumi.Integration.Tests"] = ["src/Core/Orisumi.Core/Orisumi.Core.csproj", "src/Backend/Orisumi.Api/Orisumi.Api.csproj"],
        };

        var report = LocReport.Create(snapshot, references);

        await Assert.That(report.Code).IsEqualTo(new LocRow("Total", 5, 100, 57));
        await Assert.That(report.Modules).IsEquivalentTo(new[]
        {
            new LocRow("Backend", 1, 60, 0),
            new LocRow("Core", 2, 40, 20),
            new LocRow("Clients", 1, 0, 7),
            new LocRow(LocReport.CrossModule, 1, 0, 30),
        });
        await Assert.That(report.Modules[^1].Name).IsEqualTo(LocReport.CrossModule);
    }

    [Test]
    public async Task HumanOutputScalesBarsToTheLongestRowAndListsUnclassifiedFiles()
    {
        var snapshot = new SourceSnapshot("worktree",
        [
            new("src/Core/Orisumi.Core/Observation.cs", "C#", SourceRole.Production, 40),
            new("stray.ts", "TypeScript", SourceRole.Unclassified, 3),
        ]);

        using var writer = new StringWriter();
        LocCommand.Write(Terminal.Create(writer), LocReport.Create(snapshot, new Dictionary<string, IReadOnlyList<string>>()));
        var text = writer.ToString();

        await Assert.That(text).Contains("Unclassified        3   1 files");
        await Assert.That(text).Contains("  stray.ts");
        await Assert.That(text).Contains("40        ·   " + new string('█', 30));
        await Assert.That(text).DoesNotContain(new string('█', 31));
    }
}
