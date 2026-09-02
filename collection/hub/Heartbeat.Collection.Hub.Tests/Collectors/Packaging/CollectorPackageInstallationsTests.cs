using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Tests.Collectors;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Packages;

public sealed class CollectorPackageInstallationsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-installations-{Guid.NewGuid():N}");

    [Fact]
    public void Install_ValidPackage_IsReopenableFromItsStableInstallationDirectory()
    {
        using var source = ManagedReferenceCollectorPackage.Create();
        var sourcePackage = LocalCollectorPackage.Load(source.Path);
        var installations = new CollectorPackageInstallations(InstallRoot);

        var installed = installations.Install(source.Path);

        Assert.Equal(sourcePackage.Manifest.PackageId, installed.Reference.PackageId);
        Assert.Equal(sourcePackage.Manifest.Version, installed.Reference.Version);
        Assert.Equal(sourcePackage.PackageContentHash, installed.Reference.PackageContentHash);
        Assert.Equal(sourcePackage.PackageContentHash, installed.Package.PackageContentHash);
        Assert.Equal(
            Path.Combine(
                installations.Root,
                sourcePackage.Manifest.PackageId,
                sourcePackage.Manifest.Version,
                sourcePackage.PackageContentHash["sha256:".Length..]),
            installed.Directory);
        Assert.True(File.Exists(Path.Combine(installed.Directory, "collector-manifest.json")));
        Assert.DoesNotContain(source.Path, installed.Directory, StringComparison.Ordinal);

        var reopened = installations.Open(installed.Reference);

        Assert.Equal(installed.Directory, reopened.Directory);
        Assert.Equal(installed.TreeContentHash, reopened.TreeContentHash);
        Assert.Equal(installed.Reference, reopened.Reference);
        Assert.Equal(installed.Directory, Assert.Single(installations.List()).Directory);
    }

    [Fact]
    public void Install_SameExactPackageTwice_ReusesTheInstallationWithoutRebuildingIt()
    {
        using var source = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(InstallRoot);

        var first = installations.Install(source.Path);
        var manifestPath = Path.Combine(first.Directory, "collector-manifest.json");
        var createdAt = Directory.GetCreationTimeUtc(first.Directory);
        var manifestWrittenAt = File.GetLastWriteTimeUtc(manifestPath);

        var second = installations.Install(source.Path);

        Assert.Equal(first.Directory, second.Directory);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(first.TreeContentHash, second.TreeContentHash);
        Assert.Equal(createdAt, Directory.GetCreationTimeUtc(second.Directory));
        Assert.Equal(manifestWrittenAt, File.GetLastWriteTimeUtc(manifestPath));
        Assert.Equal(
            first.Directory,
            Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(first.Directory)!)));
        Assert.Empty(StagingDirectories(installations.Root));
    }

    [Fact]
    public void Install_TamperedManifest_LeavesNeitherInstallationNorStagingBehind()
    {
        using var source = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(InstallRoot);
        var installed = installations.Install(source.Path);
        using var tampered = ReferenceCollectorPackageCopy.Create(source.Path);
        var manifest = tampered.ReadManifest();
        manifest["packageTypo"] = true;
        tampered.WriteManifest(manifest);

        Assert.Throws<PackageValidationException>(() => installations.Install(tampered.Path));

        Assert.Equal(installed.Directory, Assert.Single(installations.List()).Directory);
        Assert.Empty(StagingDirectories(installations.Root));
    }

    [Fact]
    public void Install_DeclaredFileMissingFromSource_LeavesNeitherInstallationNorStagingBehind()
    {
        using var source = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(InstallRoot);
        using var broken = ReferenceCollectorPackageCopy.Create(source.Path);
        File.Delete(Path.Combine(broken.Path, "schemas", "reference-segment.schema.json"));

        Assert.Throws<PackageValidationException>(() => installations.Install(broken.Path));

        Assert.Empty(installations.List());
        Assert.Empty(StagingDirectories(installations.Root));
    }

    [Fact]
    public void Install_InstallationDirectoryGainedAnUndeclaredFile_IsRejected()
    {
        using var source = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(InstallRoot);
        var installed = installations.Install(source.Path);
        File.WriteAllText(Path.Combine(installed.Directory, "smuggled.txt"), "not part of the Package");

        var error = Assert.Throws<PackageValidationException>(() => installations.Install(source.Path));

        Assert.Contains(installed.Reference.PackageId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_InstallationManifestNoLongerMatchesItsDirectory_IsRejectedAndSkippedByList()
    {
        using var source = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(InstallRoot);
        var installed = installations.Install(source.Path);
        var manifestPath = Path.Combine(installed.Directory, "collector-manifest.json");
        File.WriteAllText(manifestPath, JsonNode.Parse(File.ReadAllText(manifestPath))!.ToJsonString(
            new JsonSerializerOptions { WriteIndented = false }));

        Assert.Throws<PackageValidationException>(() => installations.Open(installed.Reference));
        Assert.False(installations.TryOpen(installed.Reference, out var reopened));
        Assert.Null(reopened);
        Assert.Empty(installations.List());
    }

    [Fact]
    public void List_SkipsUnfinishedStagingAndCorruptedDirectories()
    {
        using var source = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(InstallRoot);
        var installed = installations.Install(source.Path);
        var versionRoot = Path.GetDirectoryName(installed.Directory)!;
        CopyTree(installed.Directory, Path.Combine(versionRoot, ".staging-2d4f1c"));
        Directory.CreateDirectory(Path.Combine(versionRoot, new string('a', 64)));

        var listed = installations.List();

        Assert.Equal(installed.Directory, Assert.Single(listed).Directory);
        Assert.Equal(installed.Directory, Assert.Single(installations.List(installed.Reference.PackageId)).Directory);
        Assert.Empty(installations.List("heartbeat.collector.absent"));
    }

    [Fact]
    public void List_PackageIdTraversalCannotReadAnInstallationOutsideRoot()
    {
        using var source = ManagedReferenceCollectorPackage.Create();
        var outside = new CollectorPackageInstallations(Path.Combine(_root, "outside"));
        var external = outside.Install(source.Path);
        var installations = new CollectorPackageInstallations(InstallRoot);

        Assert.Throws<ArgumentException>(() => installations.List(
            Path.Combine("..", "outside", external.Reference.PackageId)));
    }

    [Fact]
    public void List_PackageRootSymlinkOutsideRoot_IsNotFollowed()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var source = ManagedReferenceCollectorPackage.Create();
        var outside = new CollectorPackageInstallations(Path.Combine(_root, "outside"));
        var external = outside.Install(source.Path);
        Directory.CreateDirectory(InstallRoot);
        Directory.CreateSymbolicLink(
            Path.Combine(InstallRoot, external.Reference.PackageId),
            Path.Combine(outside.Root, external.Reference.PackageId));
        var installations = new CollectorPackageInstallations(InstallRoot);

        Assert.Empty(installations.List(external.Reference.PackageId));
    }

    [Fact]
    public void TryOpen_ReferenceThatWasNeverInstalled_ReturnsFalseWithoutInstallation()
    {
        var installations = new CollectorPackageInstallations(InstallRoot);

        var opened = installations.TryOpen(
            new CollectorPackageReference("heartbeat.collector.absent", "1.0.0", "sha256:" + new string('0', 64)),
            out var installation);

        Assert.False(opened);
        Assert.Null(installation);
        Assert.Empty(installations.List());
    }

    /// <summary>
    /// 共享 Installation module 的声明面（字段、属性、构造器、方法签名、嵌套类型，含非公开成员）里不
    /// 出现任何宿主专属类型，Hub 程序集也不引用 VRChat / Desktop 程序集。这不是 IL 级零依赖证明。
    /// </summary>
    [Fact]
    public void InstallationModule_DeclaredSurface_NamesNoBrowserVRChatOrDesktopType()
    {
        var hub = typeof(CollectorPackageInstallations).Assembly;

        Assert.DoesNotContain(
            hub.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty),
            name => name.Contains("VRChat", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Heartbeat.Desktop", StringComparison.OrdinalIgnoreCase));

        const BindingFlags declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;
        Type[] moduleTypes =
        [
            typeof(CollectorPackageInstallations),
            typeof(CollectorPackageInstallation),
            typeof(CollectorPackageReference)
        ];
        var hostSpecific = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in moduleTypes)
        {
            foreach (var field in type.GetFields(declared))
                Inspect(field.FieldType);
            foreach (var property in type.GetProperties(declared))
                Inspect(property.PropertyType);
            foreach (var constructor in type.GetConstructors(declared))
                foreach (var parameter in constructor.GetParameters())
                    Inspect(parameter.ParameterType);
            foreach (var method in type.GetMethods(declared))
            {
                Inspect(method.ReturnType);
                foreach (var parameter in method.GetParameters())
                    Inspect(parameter.ParameterType);
            }
            foreach (var nested in type.GetNestedTypes(declared))
                Inspect(nested);
        }

        Assert.Empty(hostSpecific);

        void Inspect(Type candidate)
        {
            var name = candidate.FullName ?? candidate.Name;
            if (name.Contains("Browser", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VRChat", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Desktop", StringComparison.OrdinalIgnoreCase))
                hostSpecific.Add(name);
            if (candidate.HasElementType && candidate.GetElementType() is { } element)
                Inspect(element);
            foreach (var argument in candidate.GenericTypeArguments)
                Inspect(argument);
        }
    }

    private string InstallRoot => Path.Combine(_root, "collector-packages");

    private static IEnumerable<string> StagingDirectories(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, ".staging-*", SearchOption.AllDirectories)
            : [];

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
