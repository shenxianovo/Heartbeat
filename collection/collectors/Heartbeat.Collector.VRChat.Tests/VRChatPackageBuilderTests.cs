using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatPackageBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-vrchat-package-builder-{Guid.NewGuid():N}");

    [Fact]
    public void Create_CodeBearingDllChangesWithSameApphostAndVersion_ProducesDifferentExactPackages()
    {
        var firstSource = CreatePublishDirectory("first-publish", [0x01, 0x02, 0x03]);
        var secondSource = CreatePublishDirectory("second-publish", [0x03, 0x02, 0x01]);
        var firstDirectory = Path.Combine(_root, "first-package");
        var secondDirectory = Path.Combine(_root, "second-package");

        VRChatPackageBuilder.Create(firstSource, firstDirectory);
        VRChatPackageBuilder.Create(secondSource, secondDirectory);

        var first = LocalCollectorPackage.Load(firstDirectory);
        var second = LocalCollectorPackage.Load(secondDirectory);
        Assert.Equal("heartbeat.collector.vrchat", first.Manifest.PackageId);
        Assert.Equal("0.1.0", first.Manifest.Version);
        Assert.Equal("VRChat", first.Manifest.Presentation?.DisplayName);
        Assert.Equal("account", first.Manifest.DefaultInstance?.SubjectKind);
        Assert.Equal(1, first.Manifest.DefaultInstance?.ConfigVersion);
        Assert.Equal(first.Manifest.Version, second.Manifest.Version);
        Assert.Equal(
            Assert.Single(first.Artifacts).ContentHash,
            Assert.Single(second.Artifacts).ContentHash);
        Assert.NotEqual(first.PackageContentHash, second.PackageContentHash);
    }

    private string CreatePublishDirectory(string name, byte[] codeBytes)
    {
        var source = Path.Combine(_root, name);
        Directory.CreateDirectory(source);
        var executableName = OperatingSystem.IsWindows()
            ? "Heartbeat.Collector.VRChat.exe"
            : "Heartbeat.Collector.VRChat";
        File.WriteAllBytes(Path.Combine(source, executableName), [0x10, 0x20, 0x30]);
        File.WriteAllBytes(Path.Combine(source, "Heartbeat.Collector.VRChat.dll"), codeBytes);
        var contracts = Path.Combine(source, "contracts", "facts");
        Directory.CreateDirectory(contracts);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "contracts", "facts", "vrchat-presence-segment.schema.json"),
            Path.Combine(contracts, "vrchat-presence-segment.schema.json"));
        return source;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
