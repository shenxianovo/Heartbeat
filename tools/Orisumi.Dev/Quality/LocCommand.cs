using System.CommandLine;
using System.Text.Json;
using System.Xml.Linq;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed record LocRoleRow(string Role, int Files, int Lines);

/// 一组代码文件的行数；实现包括生产代码和开发工具。
internal sealed record LocRow(string Name, int Files, int Implementation, int Tests);

internal sealed record LocReport(
    string Revision,
    LocRow Code,
    IReadOnlyList<LocRoleRow> Roles,
    IReadOnlyList<LocRow> Modules,
    IReadOnlyList<LocRow> Languages,
    IReadOnlyList<string> UnclassifiedPaths)
{
    /// 引用的生产项目不止一个模块，或没有引用生产项目的测试项目。
    public const string CrossModule = "Cross-module";

    /// <param name="testProjectReferences">tests/ 下每个测试项目引用的项目路径，键为项目目录名。</param>
    public static LocReport Create(
        SourceSnapshot snapshot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> testProjectReferences)
    {
        var code = snapshot.Files
            .Where(file => file.Role is SourceRole.Production or SourceRole.Test or SourceRole.Tooling)
            .ToArray();
        var roles = snapshot.Files
            .GroupBy(file => file.Role)
            .OrderBy(group => group.Key)
            .Select(group => new LocRoleRow(group.Key.ToString(), group.Count(), group.Sum(file => file.Lines)))
            .ToArray();
        var modules = Rows(code, file => ModuleFor(file, testProjectReferences))
            .OrderBy(row => row.Name == CrossModule)
            .ThenByDescending(row => row.Implementation)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();
        var languages = Rows(code, file => file.Language)
            .OrderByDescending(row => row.Implementation)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();
        return new LocReport(snapshot.Revision, Row("Total", code), roles, modules, languages, snapshot.UnclassifiedPaths);
    }

    private static IEnumerable<LocRow> Rows(IEnumerable<SourceFileMetric> files, Func<SourceFileMetric, string> key) =>
        files.GroupBy(key, StringComparer.Ordinal).Select(group => Row(group.Key, [.. group]));

    private static LocRow Row(string name, SourceFileMetric[] files) => new(
        name,
        files.Length,
        files.Where(file => file.Role != SourceRole.Test).Sum(file => file.Lines),
        files.Where(file => file.Role == SourceRole.Test).Sum(file => file.Lines));

    private static string ModuleFor(
        SourceFileMetric file,
        IReadOnlyDictionary<string, IReadOnlyList<string>> testProjectReferences)
    {
        if (file.Role == SourceRole.Test && file.Path.Split('/') is ["tests", var project, ..])
        {
            var referenced = testProjectReferences.GetValueOrDefault(project, [])
                .Select(PathModule)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return referenced is [var module] ? module : CrossModule;
        }
        return PathModule(file.Path) ?? CrossModule;
    }

    /// 路径所在的模块；tests/ 下的项目由它引用的项目决定，这里返回 null。
    private static string? PathModule(string path) => path.Split('/') switch
    {
        ["src", "Docs", ..] => "Docs site",
        ["src", var area, ..] => area,
        ["tools", "Orisumi.Dev", ..] => "Developer CLI",
        ["tests", ..] => null,
        _ => "Repository tooling",
    };
}

internal sealed class LocCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
    /// 最长的一行（实现加测试）画满的格数。
    private const int BarWidth = 30;

    public Command CreateCommand()
    {
        var command = new Command("loc", "Count current effective LOC without running tests or analyzers");
        var json = new Option<bool>("--json") { Description = "Print the report as JSON" };
        command.Options.Add(json);
        command.SetAction((parse, token) => RunAsync(parse.GetValue(json), token));
        return command;
    }

    public async Task<int> RunAsync(bool json, CancellationToken cancellationToken)
    {
        var snapshot = await new GitSourceReader(repository, runner).ReadWorktreeAsync(cancellationToken);
        var references = await ReadTestProjectReferencesAsync(snapshot, cancellationToken);
        var report = LocReport.Create(snapshot, references);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions.Indented));
            return 0;
        }

        Write(Terminal.Create(output), report);
        return 0;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadTestProjectReferencesAsync(
        SourceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            if (file.Path.Split('/') is not ["tests", var project, var name]
                || !name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
            var path = repository.Path("tests", project, name);
            var directory = Path.GetDirectoryName(path)!;
            await using var stream = File.OpenRead(path);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            references[project] = [.. document.Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")?.Value)
                .OfType<string>()
                .Select(include => Path.GetRelativePath(
                    repository.Root, Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/'))))
                    .Replace('\\', '/'))];
        }
        return references;
    }

    internal static void Write(IAnsiConsole console, LocReport report)
    {
        var roles = report.Roles.ToDictionary(row => row.Role, row => row, StringComparer.Ordinal);
        int Lines(SourceRole role) => roles.GetValueOrDefault(role.ToString())?.Lines ?? 0;
        var outside = Lines(SourceRole.Documentation) + Lines(SourceRole.Build) + Lines(SourceRole.Generated);

        Terminal.Heading(console, "Effective LOC", "worktree", "[grey]blank and comment-only lines excluded[/]");
        var fields = new List<(string, string)>
        {
            ("Code", $"{Terminal.Number(report.Code.Implementation + report.Code.Tests),7}   "
                + $"[grey]{Terminal.Number(report.Code.Implementation)} impl + {Terminal.Number(report.Code.Tests)} tests[/]"),
            ("Outside code", $"{Terminal.Number(outside),7}   [grey]{Terminal.Number(Lines(SourceRole.Documentation))} docs · "
                + $"{Terminal.Number(Lines(SourceRole.Build))} build · {Terminal.Number(Lines(SourceRole.Generated))} generated[/]"),
        };
        if (roles.GetValueOrDefault(nameof(SourceRole.Unclassified)) is { } unclassified)
            fields.Add(("Unclassified", $"[yellow]{Terminal.Number(unclassified.Lines),7}[/]   [grey]{Terminal.Number(unclassified.Files)} files, listed at the end[/]"));
        Terminal.Fields(console, fields);

        var rows = report.Modules.Concat(report.Languages).ToArray();
        var width = Math.Max(rows.Max(row => (int?)row.Name.Length) ?? 0, "Language".Length);
        var linesPerCell = Math.Max(rows.Max(row => (int?)(row.Implementation + row.Tests)) ?? 0, 1) / (double)BarWidth;
        console.WriteLine();
        WriteTable(console, "Module", $"[{Implementation}]█[/] impl  [{Tests}]░[/] tests", report.Modules, report.Code, width, linesPerCell);
        console.WriteLine();
        WriteTable(console, "Language", "", report.Languages, report.Code, width, linesPerCell);

        if (report.UnclassifiedPaths.Count > 0)
        {
            console.WriteLine();
            console.MarkupLine("[yellow]Unclassified files[/]");
            foreach (var path in report.UnclassifiedPaths) console.MarkupLine($"  {Terminal.Esc(path)}");
        }
    }

    private const string Implementation = "deepskyblue3";
    private const string Tests = "green";

    private static void WriteTable(
        IAnsiConsole console, string title, string legend, IReadOnlyList<LocRow> rows, LocRow total, int width,
        double linesPerCell)
    {
        console.MarkupLine($"  [grey]{title.PadRight(width)} {"Impl",8} {"Tests",8}[/]" + (legend.Length > 0 ? "   " + legend : ""));
        foreach (var row in rows)
        {
            console.MarkupLine(Columns(row, width) + "   "
                + $"[{Implementation}]{Bar(row.Implementation, linesPerCell, '█', "▏")}[/]"
                + $"[{Tests}]{Bar(row.Tests, linesPerCell, '░', "░")}[/]");
        }
        console.MarkupLine($"[bold]{Columns(total, width)}[/]");
    }

    private static string Columns(LocRow row, int width) =>
        $"  {Terminal.Esc(row.Name.PadRight(width))} {Count(row.Implementation)} {Count(row.Tests)}";

    /// 右对齐到 8 列；零写成灰色的点。
    private static string Count(int lines) => lines == 0 ? new string(' ', 7) + "[grey]·[/]" : Terminal.Number(lines).PadLeft(8);

    /// 不足半格但不为零时画成 <paramref name="sliver"/>，让很小的数量仍然可见。
    private static string Bar(int lines, double linesPerCell, char cell, string sliver)
    {
        if (lines == 0) return "";
        var cells = (int)Math.Round(lines / linesPerCell);
        return cells == 0 ? sliver : new string(cell, cells);
    }
}
