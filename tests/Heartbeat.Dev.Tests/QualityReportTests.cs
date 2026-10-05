using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class QualityReportTests
{
    [Fact]
    public void ComparesOnlyProductionLinesByLanguage()
    {
        var baseline = new SourceSnapshot("base", [
            new("src/A.cs", "C#", SourceRole.Production, 10),
            new("tests/A.cs", "C#", SourceRole.Test, 20),
        ]);
        var current = new SourceSnapshot("worktree", [
            new("src/A.cs", "C#", SourceRole.Production, 14),
            new("src/app.ts", "TypeScript", SourceRole.Production, 5),
            new("tests/A.cs", "C#", SourceRole.Test, 24),
        ]);

        var report = QualityCommand.CompareLines(baseline, current);

        Assert.Equal(9, report.ProductionDelta);
        Assert.Equal(24, report.CurrentTestLines);
        Assert.Collection(report.ProductionLanguages,
            csharp => Assert.Equal(new LanguageDelta("C#", 10, 14, 4), csharp),
            typescript => Assert.Equal(new LanguageDelta("TypeScript", 0, 5, 5), typescript));
    }
}
