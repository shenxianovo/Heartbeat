using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ErosionScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-erosion-{Guid.NewGuid():N}");

    [Fact]
    public void MeasuresTheWholeExpressionBodiedLambda()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), """
            class A
            {
                static System.Func<int> F() => () =>
                {
                    var x = 1;
                    return x;
                };
            }
            """);

        var metric = Assert.Single(ErosionScanner.FromCSharpAnalyzer(_root,
            [new ComplexityHotspot("C#", "src/A.cs", 3, "F", 1)]));

        Assert.Equal(5, metric.Lines);
    }

    [Fact]
    public void DistinguishesOverloadsAndContainingTypesAcrossLineShifts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var path = Path.Combine(_root, "src", "A.cs");
        File.WriteAllText(path, """
            class A {
                int Run(int value) => value;
                string Run(string value) => value;
            }
            class B {
                int Run(int value) => value;
            }
            """);
        var diagnostics = new[] {
            new ComplexityHotspot("C#", "src/A.cs", 2, "Run", 12),
            new ComplexityHotspot("C#", "src/A.cs", 3, "Run", 13),
            new ComplexityHotspot("C#", "src/A.cs", 6, "Run", 14),
        };
        var before = ErosionScanner.FromCSharpAnalyzer(_root, diagnostics);
        Assert.Equal(3, before.Select(item => item.Symbol).Distinct().Count());
        File.WriteAllText(path, "\n" + File.ReadAllText(path));
        var after = ErosionScanner.FromCSharpAnalyzer(_root,
            diagnostics.Select(item => item with { Line = item.Line + 1 }).ToArray());
        Assert.Equal(before.Select(item => item.Symbol), after.Select(item => item.Symbol));
    }

    [Fact]
    public void CountsPrimaryConstructorHeaderAndTopLevelStatementsWithoutUnrelatedMembers()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), """
            System.Console.WriteLine("hello");
            class A(int value)
            {
                public int Value = value;
                public int Unrelated()
                {
                    return 42;
                }
            }
            """);
        var metrics = ErosionScanner.FromCSharpAnalyzer(_root,
            [new("C#", "src/A.cs", 1, "<Main>$", 1), new("C#", "src/A.cs", 2, ".ctor", 1, 7)]);
        Assert.Equal([1, 2], metrics.Select(item => item.Lines));
    }

    [Fact]
    public void CountsRawStringContentsWithoutTreatingThemAsSyntaxOrComments()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), """"
            class A
            {
                string Text() => """
                    "; } // text
                    /* more text
                    """;
            }
            """");
        var metric = Assert.Single(ErosionScanner.FromCSharpAnalyzer(_root,
            [new("C#", "src/A.cs", 3, "Text", 1)]));
        Assert.Equal(4, metric.Lines);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
