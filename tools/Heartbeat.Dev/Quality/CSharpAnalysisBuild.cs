using System.Text;
using System.Xml.Linq;

namespace Heartbeat.Dev;

/// <summary>Compile every C# project for metrics; native packaging is verified by platform scenarios.</summary>
internal static class CSharpAnalysisBuild
{
    internal sealed record NativeProject(string Path, bool WinUI);
    internal sealed record Plan(string SharedSolution, IReadOnlyList<NativeProject> NativeProjects);

    internal static Plan Prepare(string root, string outputSolution)
    {
        var solutionPath = Path.Combine(root, "Heartbeat.slnx");
        var solution = XDocument.Load(solutionPath);
        var native = new List<NativeProject>();
        foreach (var project in solution.Descendants("Project").ToArray())
        {
            var path = Path.GetFullPath(Path.Combine(root, project.Attribute("Path")!.Value));
            var properties = XDocument.Load(path);
            var winUI = properties.Descendants("UseWinUI").Any(item => item.Value == "true");
            var macOS = properties.Descendants("TargetFramework").Any(item => item.Value.Contains("-macos", StringComparison.Ordinal));
            if (winUI || macOS)
            {
                native.Add(new NativeProject(path, winUI));
                project.Remove();
            }
            else project.SetAttributeValue("Path", path);
        }
        if (native.Count == 0) return new Plan(solutionPath, native);
        solution.Save(outputSolution);
        return new Plan(outputSolution, native);
    }

    public static async Task<ProcessResult> RunAsync(
        string root, bool restore, string artifactDirectory, string label, string metricsProps,
        ICollection<string> commands, CancellationToken cancellationToken)
    {
        var plan = Prepare(root, Path.Combine(artifactDirectory, label + "-managed.slnx"));
        var diagnostics = new StringBuilder();
        var properties = new[] { "-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=", $"-p:CustomBeforeMicrosoftCommonProps={metricsProps}" };
        var shared = new List<string>
        {
            "build", plan.SharedSolution, "--no-incremental", "--disable-build-servers",
            "--verbosity", "minimal", "--maxcpucount:1",
        };
        shared.AddRange(properties);
        if (!restore) shared.Add("--no-restore");
        var result = await RunStepAsync(shared);
        if (result.ExitCode != 0) return result;
        foreach (var project in plan.NativeProjects)
        {
            var args = new List<string>
            {
                "msbuild", project.Path,
                "-t:ResolveReferences;GenerateGlobalUsings;GenerateAssemblyInfo;CoreCompile",
                "-p:BuildProjectReferences=false", "-p:UseSharedCompilation=false", "-verbosity:minimal", "-maxcpucount:1",
            };
            args.AddRange(properties);
            if (restore) args.Add("-restore");
            // Manifest merging uses Windows-only mt.exe; it does not participate in C# metrics.
            if (project.WinUI) args.Add("-p:WindowsAppSDKSelfContained=false");
            result = await RunStepAsync(args);
            if (result.ExitCode != 0) return result;
            var directory = Path.GetDirectoryName(project.Path)! + Path.DirectorySeparatorChar;
            if (!result.StdOut.Contains(directory, StringComparison.Ordinal) || !result.StdOut.Contains("CA1502", StringComparison.Ordinal))
                return new ProcessResult(1, diagnostics.ToString(), $"No native C# metrics emitted for {project.Path}.");
        }
        return new ProcessResult(0, diagnostics.ToString(), string.Empty);

        async Task<ProcessResult> RunStepAsync(IReadOnlyList<string> args)
        {
            commands.Add("dotnet " + string.Join(' ', args) + " (C# metrics; no native packaging)");
            var step = await ProcessRunner.CaptureAsync(root, "dotnet", args, cancellationToken,
                new Dictionary<string, string?> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US", ["VSLANG"] = "1033" });
            diagnostics.AppendLine(step.StdOut).AppendLine(step.StdErr);
            return step.ExitCode == 0 ? step : new ProcessResult(step.ExitCode, diagnostics.ToString(), step.StdErr);
        }
    }
}
