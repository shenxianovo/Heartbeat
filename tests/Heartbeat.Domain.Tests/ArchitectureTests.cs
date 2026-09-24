using System.Xml.Linq;

namespace Heartbeat.Domain.Tests;

public sealed class ArchitectureTests
{
    private static readonly string[] GenericProjectFolders = ["Backend", "Hub", "Contracts"];

    [Fact]
    public void DomainDoesNotDependOnEntityFramework()
    {
        var references = typeof(Heartbeat.Recording.Record).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference =>
            reference.Name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DomainDoesNotDependOnOtherHeartbeatLayers()
    {
        var references = typeof(Heartbeat.Recording.Record).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference =>
            reference.Name?.StartsWith("Heartbeat.", StringComparison.Ordinal) == true);
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Backend", "Heartbeat.Domain", "Heartbeat.Domain.csproj"));
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    [Fact]
    public void BackendHubAndContractsDoNotReferenceOptionalCollectors()
    {
        var root = RepositoryRoot();
        var projects = GenericProjectFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, "src", folder), "*.csproj", SearchOption.AllDirectories));
        foreach (var path in projects)
        {
            var references = XDocument.Load(path).Descendants("ProjectReference")
                .Select(element => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!,
                    element.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar))));
            Assert.DoesNotContain(references, reference => reference.StartsWith(
                Path.Combine(root, "src", "Collectors") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Heartbeat.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot find Heartbeat.slnx above the test assembly.");
    }
}
