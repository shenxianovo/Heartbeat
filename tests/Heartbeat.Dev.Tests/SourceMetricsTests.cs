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
}
