using System.Xml.Linq;

namespace Heartbeat.Dev.Tests;

public sealed class CSharpAnalysisBuildTests
{
    [Fact]
    public void NativeHostsRemainExplicitAnalysisTargetsWhileSharedBuildUsesAbsolutePaths()
    {
        var root = Directory.CreateTempSubdirectory("heartbeat-analysis-plan-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Heartbeat.slnx"), """
                <Solution><Folder Name="/src/">
                  <Project Path="Core.csproj" /><Project Path="Mac.csproj" /><Project Path="Windows.csproj" />
                </Folder></Solution>
                """);
            File.WriteAllText(Path.Combine(root, "Core.csproj"), "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Mac.csproj"), "<Project><PropertyGroup><TargetFramework>net10.0-macos</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Windows.csproj"), "<Project><PropertyGroup><UseWinUI>true</UseWinUI></PropertyGroup></Project>");
            var plan = CSharpAnalysisBuild.Prepare(root, Path.Combine(root, "analysis.slnx"));
            Assert.Equal(2, plan.NativeProjects.Count);
            Assert.Contains(plan.NativeProjects, project => project.Path == Path.Combine(root, "Mac.csproj") && !project.WinUI);
            Assert.Contains(plan.NativeProjects, project => project.Path == Path.Combine(root, "Windows.csproj") && project.WinUI);
            Assert.Equal(Path.Combine(root, "Core.csproj"), Assert.Single(XDocument.Load(plan.SharedSolution).Descendants("Project")).Attribute("Path")!.Value);
            Assert.Equal(3, XDocument.Load(Path.Combine(root, "Heartbeat.slnx")).Descendants("Project").Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
