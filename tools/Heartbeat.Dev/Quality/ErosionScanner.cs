using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Heartbeat.Dev;

internal sealed record FunctionMetric(
    string Language,
    string Path,
    int Line,
    string Symbol,
    int Complexity,
    int Lines,
    int Column = 1,
    int EndLine = 0,
    int EndColumn = 0,
    string Kind = "function")
{
    public long Burden => (long)Complexity * Lines;
    [System.Text.Json.Serialization.JsonIgnore]
    public ComplexityHotspot Hotspot => new(Language, Path, Line, Symbol, Complexity, Column);
}

internal static class ErosionScanner
{
    public static IReadOnlyList<FunctionMetric> FromCSharpAnalyzer(
        string root,
        IReadOnlyList<ComplexityHotspot> functions)
    {
        var metrics = new List<FunctionMetric>();
        foreach (var file in functions.GroupBy(item => item.Path))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(System.IO.Path.Combine(root, file.Key)),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14));
            var syntax = tree.GetRoot();
            var text = tree.GetText();
            foreach (var function in file)
            {
                var position = text.Lines[function.Line - 1].Start + function.Column - 1;
                var node = function.Symbol == "<Main>$" ? syntax
                    : syntax.FindToken(position).Parent?.AncestorsAndSelf().FirstOrDefault(item =>
                        IsFunction(item) || item is TypeDeclarationSyntax);
                if (node is null)
                    throw new InvalidDataException($"Cannot locate C# function at {function.Path}:{function.Line}:{function.Column}.");
                var symbol = node is CompilationUnitSyntax ? "<Main>$"
                    : node is TypeDeclarationSyntax ? $"{Identity(node)}/{function.Symbol}"
                    : Identity(node);
                metrics.Add(new FunctionMetric("C#", function.Path, function.Line, symbol,
                    function.Complexity, CountTokenLines(node), function.Column));
            }
        }
        return metrics;
    }

    public static IReadOnlyList<FunctionMetric> ParseTypeScriptSpans(string json) =>
        JsonSerializer.Deserialize<FunctionMetric[]>(json, JsonOptions.Indented) ?? [];

    private static bool IsFunction(SyntaxNode node) => node is
        BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
        or AnonymousFunctionExpressionSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax;

    private static string Identity(SyntaxNode node) => string.Join("/", node.AncestorsAndSelf()
        .Where(item => IsFunction(item) || item is BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
        .Reverse().Select(Name));

    private static string Name(SyntaxNode node) => node switch
    {
        BaseNamespaceDeclarationSyntax ns => Compact(ns.Name),
        TypeDeclarationSyntax type => $"{type.Identifier.ValueText}`{type.TypeParameterList?.Parameters.Count ?? 0}",
        MethodDeclarationSyntax method => $"{method.ExplicitInterfaceSpecifier}{method.Identifier.ValueText}`{method.TypeParameterList?.Parameters.Count ?? 0}{Parameters(method.ParameterList)}",
        ConstructorDeclarationSyntax constructor => $"{(constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor")}{Parameters(constructor.ParameterList)}",
        DestructorDeclarationSyntax destructor => $"~{destructor.Identifier.ValueText}()",
        OperatorDeclarationSyntax op => $"operator{op.OperatorToken}{Parameters(op.ParameterList)}",
        ConversionOperatorDeclarationSyntax conversion => $"{conversion.ImplicitOrExplicitKeyword} operator {Compact(conversion.Type)}{Parameters(conversion.ParameterList)}",
        LocalFunctionStatementSyntax local => $"{local.Identifier.ValueText}`{local.TypeParameterList?.Parameters.Count ?? 0}{Parameters(local.ParameterList)}",
        PropertyDeclarationSyntax property => $"{property.ExplicitInterfaceSpecifier}{property.Identifier.ValueText}",
        IndexerDeclarationSyntax indexer => $"{indexer.ExplicitInterfaceSpecifier}this{Parameters(indexer.ParameterList)}",
        AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
        AnonymousFunctionExpressionSyntax lambda => $"lambda#{LambdaOrdinal(lambda)}",
        _ => throw new InvalidDataException($"Unsupported function syntax: {node.Kind()}"),
    };

    private static string Parameters(BaseParameterListSyntax parameters) =>
        "(" + string.Join(",", parameters.Parameters.Select(parameter =>
            $"{string.Join(' ', parameter.Modifiers.Select(token => token.ValueText))}:{(parameter.Type is null ? "" : Compact(parameter.Type))}")) + ")";

    private static string Compact(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));

    private static int LambdaOrdinal(AnonymousFunctionExpressionSyntax lambda)
    {
        var owner = lambda.Ancestors().First(IsFunction);
        return owner.DescendantNodes(descendIntoChildren: node => node == owner || !IsFunction(node))
            .OfType<AnonymousFunctionExpressionSyntax>().TakeWhile(node => node != lambda).Count();
    }

    private static int CountTokenLines(SyntaxNode node)
    {
        var lines = new HashSet<int>();
        foreach (var token in FunctionTokens(node).Where(token => !token.IsMissing))
        {
            var span = token.GetLocation().GetLineSpan();
            for (var line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++) lines.Add(line);
        }
        return Math.Max(1, lines.Count);
    }

    private static IEnumerable<SyntaxToken> FunctionTokens(SyntaxNode node) => node switch
    {
        CompilationUnitSyntax unit => unit.Members.OfType<GlobalStatementSyntax>().SelectMany(item => item.DescendantTokens()),
        TypeDeclarationSyntax type => type.DescendantTokens().Where(token =>
            type.OpenBraceToken.IsMissing || token.SpanStart < type.OpenBraceToken.SpanStart)
            .Concat(type.Members.SelectMany(member => member switch
            {
                FieldDeclarationSyntax field => field.Declaration.Variables.Where(item => item.Initializer is not null)
                    .SelectMany(item => item.Initializer!.DescendantTokens()),
                PropertyDeclarationSyntax { Initializer: not null } property => property.Initializer.DescendantTokens(),
                _ => [],
            })),
        _ => node.DescendantTokens(),
    };
}
