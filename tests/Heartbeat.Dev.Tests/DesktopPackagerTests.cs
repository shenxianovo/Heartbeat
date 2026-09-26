using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DesktopPackagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("heartbeat package tests ").FullName;

    [Theory]
    [InlineData("osx-arm64", "osx-arm64", "Heartbeat Dev.app")]
    [InlineData("osx-x64", "osx-arm64", "Heartbeat Dev.app")]
    [InlineData("win-x64", "win-x64", "Heartbeat Dev")]
    [InlineData("win-arm64", "win-x64", "Heartbeat Dev")]
    public async Task BuildsAndReplacesOnlyTheNamedArtifact(string runtime, string host, string name)
    {
        var repository = new RepositoryContext(_root);
        var output = Path.Combine(_root, "output with spaces");
        var options = DesktopPackageOptions.Create(repository, runtime, output, host);
        Directory.CreateDirectory(Path.Combine(output, name));
        File.WriteAllText(Path.Combine(output, name, "old"), "previous");
        File.WriteAllText(Path.Combine(output, "unrelated"), "keep");
        var runner = new PackageTestRunner();
        var result = await new DesktopPackager(repository, runner, TextWriter.Null).PackageAsync(options, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(output, name, "old")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(output, "unrelated")));
        Assert.Equal([name], Directory.GetDirectories(output).Select(Path.GetFileName));
        var publish = Assert.Single(runner.Calls, call => call.File == "dotnet");
        Assert.Contains(runtime, publish.Args);
        if (options.IsMac)
        {
            Assert.Contains("-p:HeartbeatDevelopmentBuild=true", publish.Args);
            Assert.True(File.Exists(Path.Combine(result.ApplicationPath, "Contents", "Info.plist")));
            var libraryCalls = runner.Calls.Where(call => call.File == "codesign" &&
                call.Args[^1].EndsWith("libcoreclr.dylib", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, libraryCalls.Length);
            Assert.Contains("--sign", libraryCalls[0].Args);
            Assert.Contains("--verify", libraryCalls[1].Args);
            Assert.Equal("codesign", runner.Calls[^1].File);
            Assert.Contains("--verify", runner.Calls[^1].Args);
        }
        else
        {
            Assert.True(File.Exists(result.ApplicationPath));
            Assert.Contains("--self-contained", publish.Args);
            Assert.Single(runner.Calls);
        }
    }

    [Fact]
    public async Task MacPackageUsesThePersistentDevelopmentSigningIdentityForAllNativeCode()
    {
        var repository = new RepositoryContext(_root);
        var options = DesktopPackageOptions.Create(repository, "osx-arm64", _root, "osx-arm64");
        var runner = new PackageTestRunner();

        var result = await new DesktopPackager(repository, runner, TextWriter.Null)
            .PackageAsync(options, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var signingCalls = runner.Calls.Where(call => call.File == "codesign" && call.Args.Contains("--sign")).ToArray();
        Assert.NotEmpty(signingCalls);
        Assert.All(signingCalls, call => Assert.Contains(new string('A', 40), call.Args));
        Assert.DoesNotContain(signingCalls, call => call.Args.Contains("-"));
    }

    [Fact]
    public async Task MissingDevelopmentSigningIdentityFailsBeforePublishing()
    {
        var repository = new RepositoryContext(_root);
        var options = DesktopPackageOptions.Create(repository, "osx-arm64", _root, "osx-arm64");
        var runner = new PackageTestRunner(hasSigningIdentity: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DesktopPackager(repository, runner, TextWriter.Null).PackageAsync(options, CancellationToken.None));

        Assert.Contains("Heartbeat Development", error.Message);
        Assert.Equal(["security"], runner.Calls.Select(call => call.File));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("sips")]
    [InlineData("iconutil")]
    [InlineData("codesign")]
    [InlineData("verify-signature")]
    [InlineData("sign-library")]
    [InlineData("verify-library")]
    public async Task AnyBuildFailureKeepsThePreviousArtifactAndCleansStaging(string failedStep)
    {
        var options = DesktopPackageOptions.Create(new RepositoryContext(_root), "osx-arm64", _root, "osx-arm64");
        var previous = Path.Combine(_root, "Heartbeat Dev.app");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "old"), "previous");
        var runner = new PackageTestRunner(failedStep);
        var result = await new DesktopPackager(new RepositoryContext(_root), runner, TextWriter.Null).PackageAsync(options, CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("previous", File.ReadAllText(Path.Combine(previous, "old")));
        Assert.Equal([previous], Directory.GetDirectories(_root));
        Assert.Equal(failedStep, runner.LastStep);
    }

    [Fact]
    public async Task CancellationAfterPublishPreservesTheOldPackageAndCleansStaging()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new RepositoryContext(_root);
        var options = DesktopPackageOptions.Create(repository, "win-x64", _root, "win-x64");
        var previous = Path.Combine(_root, "Heartbeat Dev");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "old"), "previous");
        var runner = new PackageTestRunner(afterPublish: cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DesktopPackager(repository, runner, TextWriter.Null).PackageAsync(options, cancellation.Token));
        Assert.True(File.Exists(Path.Combine(previous, "old")));
        Assert.Equal([previous], Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task MissingSdkOutputCannotReplaceThePreviousPackage()
    {
        var repository = new RepositoryContext(_root);
        var options = DesktopPackageOptions.Create(repository, "osx-arm64", _root, "osx-arm64");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DesktopPackager(repository, new PackageTestRunner(produceBundle: false), TextWriter.Null)
                .PackageAsync(options, CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task MetadataOnlyBundleCannotReplaceThePreviousApplication()
    {
        var repository = new RepositoryContext(_root);
        var options = DesktopPackageOptions.Create(repository, "osx-arm64", _root, "osx-arm64");
        var previous = Directory.CreateDirectory(Path.Combine(_root, "Heartbeat Dev.app")).FullName;
        File.WriteAllText(Path.Combine(previous, "old"), "working application");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DesktopPackager(repository, new PackageTestRunner(produceExecutable: false), TextWriter.Null)
                .PackageAsync(options, CancellationToken.None));
        Assert.Equal("working application", File.ReadAllText(Path.Combine(previous, "old")));
        Assert.Equal([previous], Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("osx-arm64", "win-x64")]
    [InlineData("win-x64", "osx-arm64")]
    [InlineData("linux-x64", "linux-x64")]
    public void UnsupportedPlatformsFailBeforeCreatingOutput(string runtime, string host)
    {
        var output = Path.Combine(_root, "output");
        Assert.Throws<CommandUsageException>(() => DesktopPackageOptions.Create(new RepositoryContext(_root), runtime, output, host));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("osx-arm64", "desktop")]
    [InlineData("win-arm64", "desktop-windows")]
    public void DefaultsUseHostArchitectureAndRepositoryArtifactDirectory(string host, string folder)
    {
        var options = DesktopPackageOptions.Create(new RepositoryContext(_root), hostRuntime: host);
        Assert.Equal(host, options.Runtime);
        Assert.Equal(Path.Combine(_root, ".artifacts", folder), options.OutputDirectory);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

internal sealed class PackageTestRunner(
    string? failedStep = null,
    Action? afterPublish = null,
    bool produceBundle = true,
    bool hasSigningIdentity = true,
    bool produceExecutable = true) : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];
    public string? LastStep { get; private set; }
    public string? OpenedApplication { get; private set; }
    public void OpenApplication(string path) => OpenedApplication = path;

    public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments));
        if (fileName == "security")
        {
            var standardOutput = hasSigningIdentity
                ? $"  1) {new string('A', 40)} \"{MacDevelopmentSigning.IdentityName}\"\n     1 valid identities found\n"
                : "     0 valid identities found\n";
            return Task.FromResult(new ProcessResult(0, standardOutput, ""));
        }
        if (fileName == "/usr/bin/plutil")
            return Task.FromResult(new ProcessResult(0, "Heartbeat.Desktop.Mac", ""));
        LastStep = fileName == "codesign" && arguments.Contains("--verify") ? "verify-signature" : fileName;
        if (fileName == "codesign" && arguments[^1].EndsWith(".dylib", StringComparison.Ordinal))
            LastStep = arguments.Contains("--verify") ? "verify-library" : "sign-library";
        if (LastStep == failedStep) return Task.FromResult(new ProcessResult(7, "", "build failed"));
        if (fileName == "dotnet" && produceBundle)
        {
            var appBundle = arguments.SingleOrDefault(arg => arg.StartsWith("-p:AppBundleDir=", StringComparison.Ordinal));
            if (appBundle is not null)
            {
                var contents = Path.Combine(Path.GetFullPath(appBundle["-p:AppBundleDir=".Length..], arguments[1]), "Contents");
                Directory.CreateDirectory(contents);
                if (produceExecutable)
                {
                    Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
                    File.WriteAllText(Path.Combine(contents, "MacOS", "Heartbeat.Desktop.Mac"), "executable");
                }
                File.WriteAllText(Path.Combine(contents, "Info.plist"), "<plist/>");
                var libraries = Directory.CreateDirectory(Path.Combine(contents, "MonoBundle"));
                File.WriteAllText(Path.Combine(libraries.FullName, "libcoreclr.dylib"), "unsigned library");
            }
            else
            {
                var bundle = arguments[arguments.ToList().IndexOf("-o") + 1];
                Directory.CreateDirectory(bundle);
                File.WriteAllText(Path.Combine(bundle, "Heartbeat.Desktop.Windows.exe"), "stub");
            }
            afterPublish?.Invoke();
        }
        return Task.FromResult(new ProcessResult(0, "", ""));
    }

    public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        Assert.Equal("/usr/bin/open", fileName);
        Calls.Add((fileName, arguments));
        return Task.FromResult(0);
    }
}
