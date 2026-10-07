using System.Xml.Linq;

namespace Heartbeat.Dev;

/// <summary>Compile business and developer-tool projects for semantic diagnostics.</summary>
internal static class CSharpAnalysisBuild
{
    internal static string Prepare(string root, string outputSolution)
    {
        var solution = XDocument.Load(Path.Combine(root, "Heartbeat.slnx"));
        foreach (var project in solution.Descendants("Project").ToArray())
        {
            var relative = project.Attribute("Path")!.Value.Replace('\\', '/');
            if (!relative.StartsWith("src/", StringComparison.Ordinal) && !relative.StartsWith("tools/", StringComparison.Ordinal))
                project.Remove();
            else project.SetAttributeValue("Path", Path.GetFullPath(Path.Combine(root, relative)));
        }
        solution.Save(outputSolution);
        return outputSolution;
    }

    public static async Task<ProcessResult> RunAsync(
        string root, bool restore, string artifactDirectory, string label, string metricsProps,
        ICollection<string> commands, CancellationToken cancellationToken)
    {
        var solution = Prepare(root, Path.Combine(artifactDirectory, label + "-implementation.slnx"));
        var arguments = new List<string>
        {
            "build", solution, "--no-incremental", "--disable-build-servers", "--verbosity", "minimal", "--maxcpucount:1",
            "-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=", $"-p:CustomBeforeMicrosoftCommonProps={metricsProps}",
        };
        if (!restore) arguments.Add("--no-restore");
        commands.Add("dotnet " + string.Join(' ', arguments) + " (C# implementation metrics)");
        try
        {
            return await ProcessRunner.CaptureAsync(root, "dotnet", arguments, cancellationToken,
                new Dictionary<string, string?> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US", ["VSLANG"] = "1033" });
        }
        catch (CapturedProcessCancelledException cancelled)
        {
            await File.WriteAllTextAsync(Path.Combine(artifactDirectory, label + "-csharp.log"),
                cancelled.StdOut + cancelled.StdErr, CancellationToken.None);
            throw;
        }
    }
}
