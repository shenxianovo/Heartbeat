using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatPackageReleaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-vrchat-release-{Guid.NewGuid():N}");

    [Fact]
    public async Task ShellCli_ProducesDeterministicExactReleaseMetadata()
    {
        if (OperatingSystem.IsWindows())
            return;
        var package = CreatePackage("1.2.3");
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");

        var firstResult = await RunAsync(package, "1.2.3", first);
        var secondResult = await RunAsync(package, "1.2.3", second);

        Assert.Equal(0, firstResult.ExitCode);
        Assert.Equal(0, secondResult.ExitCode);
        var artifactName = "heartbeat.collector.vrchat-1.2.3-linux-x64.zip";
        var firstArtifact = Path.Combine(first, artifactName);
        var secondArtifact = Path.Combine(second, artifactName);
        Assert.Equal(File.ReadAllBytes(firstArtifact), File.ReadAllBytes(secondArtifact));

        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "release.json")));
        var root = metadata.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("heartbeat.collector.vrchat", root.GetProperty("packageId").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal("linux", root.GetProperty("target").GetProperty("os").GetString());
        Assert.Equal("x64", root.GetProperty("target").GetProperty("arch").GetString());
        var artifact = root.GetProperty("artifact");
        Assert.Equal(new FileInfo(firstArtifact).Length, artifact.GetProperty("length").GetInt64());
        Assert.Equal(
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(firstArtifact))),
            artifact.GetProperty("sha256").GetString());
        Assert.Equal(
            $"https://heartbeat.shenxianovo.com/collector-registry/v1/packages/heartbeat.collector.vrchat/versions/1.2.3/{artifactName}",
            artifact.GetProperty("url").GetString());

        using var archive = ZipFile.OpenRead(firstArtifact);
        Assert.Contains(archive.Entries, entry => entry.FullName == "collector-manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "Heartbeat.Collector.VRChat");
        Assert.DoesNotContain(
            archive.Entries,
            entry => entry.FullName == ".heartbeat-vrchat-package-output");
    }

    [Fact]
    public async Task ShellCli_TagVersionMustMatchPackageVersion()
    {
        if (OperatingSystem.IsWindows())
            return;
        var package = CreatePackage("1.2.3");
        var output = Path.Combine(_root, "mismatch");

        var result = await RunAsync(package, "1.2.4", output);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must identify", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(output, "release.json")));
    }

    private string CreatePackage(string version)
    {
        var package = Path.Combine(_root, $"package-{version}");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "Heartbeat.Collector.VRChat"), "apphost");
        File.WriteAllText(Path.Combine(package, "Heartbeat.Collector.VRChat.dll"), "assembly");
        File.WriteAllText(
            Path.Combine(package, ".heartbeat-vrchat-package-output"),
            "heartbeat-vrchat-package-output-v1");
        File.WriteAllText(
            Path.Combine(package, "collector-manifest.json"),
            $$"""
              {
                "manifestVersion": 1,
                "packageId": "heartbeat.collector.vrchat",
                "version": "{{version}}",
                "artifacts": [
                  {
                    "artifactId": "vrchat.managed",
                    "selector": {
                      "driver": "managedProcess",
                      "os": ["linux"],
                      "arch": ["x64"]
                    },
                    "entrypoint": "Heartbeat.Collector.VRChat",
                    "size": 7,
                    "contentHash": "sha256:placeholder"
                  }
                ]
              }
              """);
        return package;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string package,
        string version,
        string output)
    {
        var start = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "package-vrchat-release.sh"));
        start.ArgumentList.Add("--package");
        start.ArgumentList.Add(package);
        start.ArgumentList.Add("--version");
        start.ArgumentList.Add(version);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Could not start package-vrchat-release.sh.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await standardOutput + await standardError);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            var gitEntry = Path.Combine(directory.FullName, ".git");
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "package-vrchat-release.sh")) &&
                (Directory.Exists(gitEntry) || File.Exists(gitEntry)))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Heartbeat repository root.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
