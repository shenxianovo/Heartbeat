using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class SourceMetricsTests
{
    [Theory]
    [InlineData("src/Backend/App.cs", "C#", "Production")]
    [InlineData("src/Frontend/App.test.tsx", "TypeScript", "Test")]
    [InlineData("tests/Heartbeat.Tests/AppTests.cs", "C#", "Test")]
    [InlineData("tools/Heartbeat.Dev/Program.cs", "C#", "Tooling")]
    [InlineData("docs/development.md", "Markdown", "Documentation")]
    [InlineData("src/Persistence/Migrations/Foo.Designer.cs", "C#", "Generated")]
    [InlineData("src/Persistence/Migrations/ModelSnapshot.cs", "C#", "Generated")]
    [InlineData("src/Frontend/package-lock.json", "", "Generated")]
    // 构建与环境描述是代码，但不是被度量的产品语料。
    [InlineData("src/Backend/Heartbeat.Api/Heartbeat.Api.csproj", "MSBuild", "Build")]
    [InlineData("Directory.Build.props", "MSBuild", "Build")]
    [InlineData("Heartbeat.slnx", "XML", "Build")]
    [InlineData("src/Collectors/Heartbeat.Collector.Desktop.Mac/Dockerfile", "Dockerfile", "Build")]
    [InlineData("compose.yaml", "YAML", "Build")]
    [InlineData(".github/workflows/verify.yml", "YAML", "Build")]
    [InlineData("src/Frontend/Heartbeat.Web/next.config.ts", "TypeScript", "Build")]
    // 认得出语言但不在任何已声明的根下：单列出来，不塞进 tooling。
    [InlineData("prototype/spike.ts", "TypeScript", "Unclassified")]
    [InlineData("Program.cs", "C#", "Unclassified")]
    public void ClassifiesTheMaintainedCorpus(string path, string language, string role)
    {
        var supported = SourceCorpus.TryClassify(path, out var actualLanguage, out var actualRole);

        if (language.Length == 0)
        {
            Assert.False(supported);
            return;
        }
        Assert.True(supported);
        Assert.Equal(language, actualLanguage);
        Assert.Equal(role, actualRole.ToString());
    }

    [Fact]
    public void CountsCodeWhileIgnoringBlankAndCommentOnlyLines()
    {
        const string source = """
            // comment
            public static class Example
            {
                /* comment */
                public static string Value => "// not a comment";
            }
            """;

        Assert.Equal(4, SourceLineCounter.Count(source, "C#"));
    }

    /// 生产 LOC 只数 src/ 下的产品语料；未归类的路径要在快照里点得出名字。
    [Fact]
    public void SnapshotSeparatesProductionFromBuildAndUnclassified()
    {
        var snapshot = new SourceSnapshot("abc1234", [
            new("src/Backend/App.cs", "C#", SourceRole.Production, 120),
            new("src/Backend/Heartbeat.Api/Heartbeat.Api.csproj", "MSBuild", SourceRole.Build, 14),
            new("compose.yaml", "YAML", SourceRole.Build, 30),
            new("tests/Heartbeat.Tests/AppTests.cs", "C#", SourceRole.Test, 200),
            new("tools/Heartbeat.Dev/Program.cs", "C#", SourceRole.Tooling, 60),
            new("prototype/spike.ts", "TypeScript", SourceRole.Unclassified, 25),
        ]);

        Assert.Equal(120, snapshot.ProductionLines);
        Assert.Equal(1, snapshot.ProductionFiles);
        Assert.Equal(44, snapshot.BuildLines);
        Assert.Equal(25, snapshot.UnclassifiedLines);
        Assert.Equal(["prototype/spike.ts"], snapshot.UnclassifiedPaths);
        // 只报目录：根下的 compose.yaml 不是「代码住的地方」。
        Assert.Equal(["prototype", "src", "tests", "tools"], snapshot.CodeRoots);
    }

    /// 基点选错时要能说出「你的源码在这些目录下」，所以 CodeRoots 不看角色只看位置。
    [Fact]
    public void CodeRootsDescribeWhereAPreRewriteTreeKeptItsCode()
    {
        var snapshot = new SourceSnapshot("0f1e2d3", [
            new("collection/agent.py", "Python", SourceRole.Unclassified, 400),
            new("server/app.py", "Python", SourceRole.Unclassified, 900),
            new("docs/notes.md", "Markdown", SourceRole.Documentation, 40),
        ]);

        Assert.Equal(0, snapshot.ProductionFiles);
        Assert.Equal(["collection", "server"], snapshot.CodeRoots);
    }
}
