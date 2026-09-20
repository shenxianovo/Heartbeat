using System.CommandLine;
using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record LocRoleRow(string Role, int Files, int Lines);

internal sealed record LocModuleRow(string Module, int Implementation, int Tests);

internal sealed record LocLanguageRow(string Language, int Implementation, int Tests);

internal sealed record LocReport(
    string Revision,
    int CountedFiles,
    int EffectiveLines,
    IReadOnlyList<LocRoleRow> Roles,
    IReadOnlyList<LocModuleRow> Modules,
    IReadOnlyList<LocLanguageRow> Languages,
    IReadOnlyList<string> UnassignedTestPaths)
{
    private const string UnassignedTests = "Unassigned tests";

    private static readonly IReadOnlyDictionary<string, string> TestModules =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Heartbeat.Domain.Tests"] = "Backend",
            ["Heartbeat.Integration.Tests"] = "Backend",
            ["Heartbeat.Collector.Desktop.Mac.Tests"] = "Collectors",
            ["Heartbeat.Desktop.Tests"] = "Desktop",
            ["Heartbeat.Hub.Tests"] = "Hub",
            ["Heartbeat.Dev.Tests"] = "Developer CLI",
        };

    public static LocReport Create(SourceSnapshot snapshot)
    {
        var roles = snapshot.Files
            .GroupBy(file => file.Role)
            .OrderBy(group => group.Key)
            .Select(group => new LocRoleRow(group.Key.ToString(), group.Count(), group.Sum(file => file.Lines)))
            .ToArray();
        var moduleFiles = snapshot.Files
            .Where(file => file.Role is SourceRole.Production or SourceRole.Test or SourceRole.Tooling)
            .Select(file => (File: file, Module: ModuleFor(file)))
            .Where(item => item.Module is not null)
            .ToArray();
        var modules = moduleFiles
            .GroupBy(item => item.Module!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new LocModuleRow(
                group.Key,
                ImplementationLines(group.Select(item => item.File)),
                Lines(group, SourceRole.Test)))
            .ToArray();
        var languages = snapshot.Files
            .Where(file => file.Role is SourceRole.Production or SourceRole.Test or SourceRole.Tooling)
            .GroupBy(file => file.Language, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new LocLanguageRow(
                group.Key,
                ImplementationLines(group),
                Lines(group, SourceRole.Test)))
            .ToArray();
        var unassigned = moduleFiles
            .Where(item => item.Module == UnassignedTests)
            .Select(item => item.File.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new LocReport(
            snapshot.Revision,
            snapshot.Files.Count,
            snapshot.Files.Sum(file => file.Lines),
            roles,
            modules,
            languages,
            unassigned);
    }

    private static int Lines(IEnumerable<(SourceFileMetric File, string? Module)> files, SourceRole role) =>
        files.Where(item => item.File.Role == role).Sum(item => item.File.Lines);

    private static int Lines(IEnumerable<SourceFileMetric> files, SourceRole role) =>
        files.Where(file => file.Role == role).Sum(file => file.Lines);

    private static int ImplementationLines(IEnumerable<SourceFileMetric> files) =>
        files.Where(file => file.Role is SourceRole.Production or SourceRole.Tooling).Sum(file => file.Lines);

    private static string? ModuleFor(SourceFileMetric file)
    {
        var segments = file.Path.Replace('\\', '/').Split('/');
        if (segments is ["src", var area, ..]) return area;
        if (segments is ["tools", "Heartbeat.Dev", ..]) return "Developer CLI";
        if (file.Role == SourceRole.Test && segments is ["tests", var project, ..])
        {
            return TestModules.GetValueOrDefault(project) ?? UnassignedTests;
        }
        if (file.Role == SourceRole.Tooling) return "Repository tooling";
        return null;
    }
}

internal sealed class LocCommand(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output)
{
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
        var report = LocReport.Create(snapshot);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions.Indented));
            return 0;
        }

        await WriteHumanAsync(report);
        return 0;
    }

    private async Task WriteHumanAsync(LocReport report)
    {
        await output.WriteLineAsync(
            $"Current effective LOC: {report.EffectiveLines:N0} across {report.CountedFiles:N0} counted files");
        await output.WriteLineAsync("  blank and comment-only lines excluded; generated and documentation roles remain separate");
        await output.WriteLineAsync();
        await output.WriteLineAsync("By role");
        await output.WriteLineAsync($"  {"Role",-16} {"Files",8} {"LOC",10}");
        foreach (var row in report.Roles)
        {
            await output.WriteLineAsync($"  {row.Role,-16} {row.Files,8:N0} {row.Lines,10:N0}");
        }
        await output.WriteLineAsync();
        await output.WriteLineAsync("By module");
        await output.WriteLineAsync($"  {"Module",-20} {"Implementation",15} {"Tests",10}");
        foreach (var row in report.Modules)
        {
            await output.WriteLineAsync(
                $"  {row.Module,-20} {row.Implementation,15:N0} {row.Tests,10:N0}");
        }
        await output.WriteLineAsync(
            $"  {"Total",-20} {report.Modules.Sum(row => row.Implementation),15:N0} "
            + $"{report.Modules.Sum(row => row.Tests),10:N0}");
        await output.WriteLineAsync();
        await output.WriteLineAsync("By language");
        await output.WriteLineAsync($"  {"Language",-16} {"Implementation",15} {"Tests",10}");
        foreach (var row in report.Languages)
        {
            await output.WriteLineAsync(
                $"  {row.Language,-16} {row.Implementation,15:N0} {row.Tests,10:N0}");
        }
        await output.WriteLineAsync(
            $"  {"Total",-16} {report.Languages.Sum(row => row.Implementation),15:N0} "
            + $"{report.Languages.Sum(row => row.Tests),10:N0}");
        if (report.UnassignedTestPaths.Count > 0)
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync("Unassigned test paths");
            foreach (var path in report.UnassignedTestPaths)
            {
                await output.WriteLineAsync($"  {path}");
            }
        }
    }
}
