using System.IO.Compression;
using System.Text.Json;
using Orisumi.Dev;
using TUnit.Core.Enums;

namespace Orisumi.Dev.Tests;

public sealed class ObserversCommandTests
{
    [Test]
    public async Task PackPublishesRuntimeAndWritesManifestsFromTheProjects()
    {
        using var fixture = new PackFixture();
        var cli = new DeveloperCli(fixture.Repository, fixture.Runner, fixture.Output, TextWriter.Null);
        var output = Path.Combine(fixture.Repository.Root, "out");
        await Assert.That(await cli.RunAsync(["observers", "pack", "foreground-state", "application-identity",
            "--target", "linux-x64", "--output", output], CancellationToken.None)).IsEqualTo(0);

        var publishes = fixture.Runner.Calls.Where(call => call.Args[0] == "publish").ToArray();
        await Assert.That(publishes.Length).IsEqualTo(3);
        await Assert.That(publishes[0].Args.Contains("-p:PublishSingleFile=true")).IsTrue();
        foreach (var publish in publishes)
            await Assert.That(publish.Args[Array.IndexOf(publish.Args, "--runtime") + 1]).IsEqualTo("linux-x64");
        await Assert.That(File.Exists(Path.Combine(output, "runtime", "Orisumi.Observer.Runtime"))).IsTrue();

        var foreground = Manifest(Path.Combine(output, "bundled", "foreground-state-0.1.0-linux-x64.zip"));
        await Assert.That(foreground.GetProperty("name").GetString()).IsEqualTo("Foreground State");
        await Assert.That(foreground.GetProperty("entry").GetString()).IsEqualTo("bin/Orisumi.Observer.ForegroundState");
        await Assert.That(foreground.GetProperty("scope").GetString()).IsEqualTo("host");
        var identity = Manifest(Path.Combine(output, "bundled", "application-identity-1.0.0-linux-x64.zip"));
        await Assert.That(identity.GetProperty("scope").GetString()).IsEqualTo("deployment");
        await Assert.That(identity.GetProperty("target").GetString()).IsEqualTo("linux-x64");
        await Assert.That(fixture.Runner.Calls.Any(call => call.File == "codesign")).IsFalse();
    }

    [Test, RunOn(OS.MacOs)]
    public async Task SigningCoversRuntimeAndEveryExecutableInThePackage()
    {
        using var fixture = new PackFixture();
        var cli = new DeveloperCli(fixture.Repository, fixture.Runner, fixture.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["observers", "pack", "foreground-state", "--target", "osx-arm64",
            "--output", Path.Combine(fixture.Repository.Root, "out"), "--sign", "-"], CancellationToken.None)).IsEqualTo(0);
        var signed = fixture.Runner.Calls.Where(call => call.File == "codesign").Select(call => Path.GetFileName(call.Args[^1])).ToArray();
        await Assert.That(signed).IsEquivalentTo(["Orisumi.Observer.Runtime", "Orisumi.Observer.ForegroundState", "libnative.dylib"]);
    }

    [Test]
    public async Task UnknownPackageDoesNotPublish()
    {
        using var fixture = new PackFixture();
        var cli = new DeveloperCli(fixture.Repository, fixture.Runner, fixture.Output, TextWriter.Null);
        await Assert.That(await cli.RunAsync(["observers", "pack", "missing", "--target", "osx-arm64", "--output", "out"],
            CancellationToken.None)).IsEqualTo(2);
        await Assert.That(fixture.Runner.Calls.Count).IsEqualTo(0);
    }

    private static JsonElement Manifest(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var stream = zip.GetEntry("observer.json")!.Open();
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private sealed class PackFixture : IDisposable
    {
        public RepositoryContext Repository { get; } = new(Path.Combine(Path.GetTempPath(), "orisumi-pack-tests-" + Guid.NewGuid().ToString("N")));
        public PackRunner Runner { get; } = new();
        public StringWriter Output { get; } = new();

        public PackFixture()
        {
            Project("Orisumi.Observer.ForegroundState", """
                <ObserverPackageId>foreground-state</ObserverPackageId><ObserverName>Foreground State</ObserverName>
                """);
            Project("Orisumi.Observer.ApplicationIdentity", """
                <ObserverPackageId>application-identity</ObserverPackageId><ObserverName>Application Identity</ObserverName>
                <ObserverScope>deployment</ObserverScope>
                """);
            Project("Orisumi.Observer.Runtime", "");
        }

        private void Project(string name, string properties)
        {
            var directory = Repository.Path("src", "Observers", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>");
        }

        public void Dispose() { Output.Dispose(); Directory.Delete(Repository.Root, true); }
    }

    /// Stands in for dotnet and codesign: publish writes the entry and a native library into the output directory.
    private sealed class PackRunner : IProcessRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = [];

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            Calls.Add((fileName, args));
            if (fileName == "dotnet" && args[0] == "msbuild")
            {
                var name = Path.GetFileNameWithoutExtension(args[1]);
                var version = name.EndsWith("ForegroundState", StringComparison.Ordinal) ? "0.1.0" : "1.0.0";
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new { Properties = new { Version = version, AssemblyName = name } }), ""));
            }
            if (fileName == "dotnet" && args[0] == "publish")
            {
                var into = args[Array.IndexOf(args, "--output") + 1];
                Directory.CreateDirectory(into);
                var project = args[1].EndsWith(".csproj", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(args[1]) : Path.GetFileName(args[1]);
                var entry = Path.Combine(into, project);
                File.WriteAllText(entry, "");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(entry, File.GetUnixFileMode(entry) | UnixFileMode.UserExecute);
                if (!args.Contains("-p:PublishSingleFile=true")) File.WriteAllText(Path.Combine(into, "libnative.dylib"), "");
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}
