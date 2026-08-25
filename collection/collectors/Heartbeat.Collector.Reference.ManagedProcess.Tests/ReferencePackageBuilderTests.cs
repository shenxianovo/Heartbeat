using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Heartbeat.Collector.Reference.ManagedProcess.Tests;

public sealed class ReferencePackageBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-reference-builder-{Guid.NewGuid():N}");

    [Fact]
    public void Create_ProducesCurrentManagedProcessArtifactAndAccountOutput()
    {
        var source = Path.Combine(_root, "source");
        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(source);
        var executableName = OperatingSystem.IsWindows()
            ? "Heartbeat.Collector.Reference.ManagedProcess.exe"
            : "Heartbeat.Collector.Reference.ManagedProcess";
        File.WriteAllText(Path.Combine(source, executableName), "reference executable");
        File.WriteAllText(Path.Combine(source, "Heartbeat.Collector.Reference.ManagedProcess.dll"), "reference assembly");

        ReferencePackageBuilder.Create(source, package);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "collector-manifest.json")))!;
        Assert.Equal("managedProcess", manifest["artifacts"]![0]!["selector"]!["driver"]!.GetValue<string>());
        Assert.Equal(CurrentOperatingSystem(), manifest["artifacts"]![0]!["selector"]!["os"]![0]!.GetValue<string>());
        Assert.Equal(CurrentArchitecture(), manifest["artifacts"]![0]!["selector"]!["arch"]![0]!.GetValue<string>());
        Assert.Equal("account", manifest["outputs"]![0]!["subjectKinds"]![0]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(package, "schemas", "reference-segment.schema.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string CurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        throw new PlatformNotSupportedException();

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException()
    };
}
