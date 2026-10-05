using System.Security.Cryptography.X509Certificates;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DevelopmentSigningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("heartbeat-signing-tests-").FullName;
    private string Keychain => Path.Combine(_root, "test.keychain-db");

    [Fact]
    public async Task SetupReusesTheSameIdentityAcrossRepeatedCallsWithoutMutatingKeychain()
    {
        var runner = new SigningRunner(Identity('A'));
        var signing = new MacDevelopmentSigning(runner, TextWriter.Null, Keychain);
        await signing.SetupAsync(CancellationToken.None);
        await signing.SetupAsync(CancellationToken.None);
        Assert.Equal(new string('A', 40), await signing.RequireIdentityAsync(CancellationToken.None));
        Assert.All(runner.Calls, args => Assert.Equal(["find-identity", "-v", "-p", "codesigning", Keychain], args));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrAmbiguousIdentityCannotBeUsedForPackaging(bool ambiguous)
    {
        var runner = new SigningRunner(ambiguous ? Identity('A') + Identity('B') : Identity('A', "Heartbeat Development Other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MacDevelopmentSigning(runner, TextWriter.Null, Keychain).RequireIdentityAsync(CancellationToken.None));
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SetupNeverReplacesAnInvalidCertificateOrIgnoresKeychainErrors(int certificateExit)
    {
        var runner = new SigningRunner("", certificateExit);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MacDevelopmentSigning(runner, TextWriter.Null, Keychain).SetupAsync(CancellationToken.None));
        Assert.Equal(["find-identity", "find-certificate"], runner.Calls.Select(args => args[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshSetupImportsOnceWithRestrictedTrustAndAlwaysCleansPrivateFiles(bool failImport)
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new SigningRunner("", 44, failImport);
        var signing = new MacDevelopmentSigning(runner, TextWriter.Null, Keychain);
        if (failImport) await Assert.ThrowsAsync<InvalidOperationException>(() => signing.SetupAsync(CancellationToken.None));
        else await signing.SetupAsync(CancellationToken.None);
        var import = Assert.Single(runner.Calls, args => args[0] == "import");
        Assert.Contains(Keychain, import);
        Assert.DoesNotContain("-A", import);
        Assert.Equal("/usr/bin/codesign", import[^1]);
        Assert.False(Directory.Exists(Path.GetDirectoryName(import[1])));
        if (failImport) Assert.DoesNotContain(runner.Calls, args => args[0] == "add-trusted-cert");
        else
        {
            var trust = Assert.Single(runner.Calls, args => args[0] == "add-trusted-cert");
            Assert.Equal(["add-trusted-cert", "-r", "trustRoot", "-p", "codeSign", "-k", Keychain, "certificate.cer"],
                trust.Take(trust.Count - 1).Append(Path.GetFileName(trust[^1])));
        }
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task GeneratedIdentityCanBeImportedByMacOSWithoutChangingUserTrust()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var runner = new ImportOnlyRunner(_root);
        async Task<ProcessResult> Security(params string[] args) =>
            await runner.CaptureAsync("security", args, null, CancellationToken.None);
        Assert.Equal(0, (await Security("create-keychain", "-p", "", Keychain)).ExitCode);
        try
        {
            var unlocked = await Security("unlock-keychain", "-p", "", Keychain);
            Assert.True(unlocked.ExitCode == 0, unlocked.StdErr);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new MacDevelopmentSigning(runner, TextWriter.Null, Keychain).SetupAsync(CancellationToken.None));
            Assert.Contains("Test stopped before modifying user trust", error.Message);
            var identities = await Security("find-identity", "-p", "codesigning", Keychain);
            Assert.Equal(0, identities.ExitCode);
            Assert.Contains("\"Heartbeat Development\"", identities.StdOut);
        }
        finally
        {
            Assert.Equal(0, (await Security("delete-keychain", Keychain)).ExitCode);
        }
    }

    private sealed class ImportOnlyRunner(string directory) : IProcessRunner
    {
        private readonly ProcessRunner _runner = new(directory);
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            arguments[0] == "add-trusted-cert"
                ? Task.FromResult(new ProcessResult(99, "", "Test stopped before modifying user trust"))
                : _runner.CaptureAsync(fileName, arguments, environment, cancellationToken);
        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static string Identity(char hash, string name = MacDevelopmentSigning.IdentityName) => $"  1) {new string(hash, 40)} \"{name}\"\n";

    private sealed class SigningRunner(string identities, int certificateExit = 0, bool failImport = false) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        private bool _imported;

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Assert.Equal("security", fileName);
            Calls.Add(arguments);
            var result = arguments[0] switch
            {
                "find-identity" => new ProcessResult(0, _imported ? Identity('B') : identities, ""),
                "find-certificate" => new ProcessResult(certificateExit, "", "certificate lookup"),
                "import" => Import(arguments[1]),
                "add-trusted-cert" => new ProcessResult(0, "", ""),
                _ => throw new InvalidOperationException(arguments[0]),
            };
            return Task.FromResult(result);
        }

        private ProcessResult Import(string path)
        {
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            using var certificate = X509Certificate2.CreateFromPemFile(path, path);
            Assert.True(certificate.HasPrivateKey);
            Assert.Equal("CN=Heartbeat Development", certificate.Subject);
            Assert.Contains(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
                oid => oid.Value == "1.3.6.1.5.5.7.3.3");
            _imported = !failImport;
            return new ProcessResult(failImport ? 1 : 0, "", failImport ? "import failed" : "");
        }

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
