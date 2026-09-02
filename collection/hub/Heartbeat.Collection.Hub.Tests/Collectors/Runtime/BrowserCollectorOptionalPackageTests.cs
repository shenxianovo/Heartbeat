using System.Text;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Runtime;

/// <summary>
/// Browser 是可选的 ExternalHost Collector。宿主里"没有 Package source"和"没有 Installation"都是
/// 合法状态：Runtime 与 loopback binding 照常构造，快照只报未安装，hello 只拒掉发起的那一条连接。
/// </summary>
public sealed class BrowserCollectorOptionalPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-browser-optional-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _time = new();
    private readonly CollectorRuntime _runtime;

    public BrowserCollectorOptionalPackageTests()
    {
        Directory.CreateDirectory(_root);
        _runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new Clock(_time)),
            appHintResolver: new Resolver());
    }

    [Fact]
    public void MissingPackageSourceDirectory_StillBuildsRuntimeAndReportsNotInstalled()
    {
        var missing = Path.Combine(_root, "CollectorPackages", "Browser");
        Assert.False(Directory.Exists(missing));

        var runtime = CreateBrowserRuntime(missing);

        Assert.False(runtime.Current.IsInstalled);
        Assert.Equal(BrowserCollectorRuntimeStatus.Waiting, runtime.Current.RuntimeStatus);
        Assert.Null(runtime.Current.SideloadDirectory);
        Assert.Null(runtime.Current.PackageVersion);
    }

    [Fact]
    public void NoPackageSourceConfigured_StillBuildsRuntimeAndReportsNotInstalled()
    {
        var runtime = CreateBrowserRuntime(packageDirectory: null);

        Assert.False(runtime.Current.IsInstalled);
        Assert.Equal(BrowserCollectorRuntimeStatus.Waiting, runtime.Current.RuntimeStatus);
        Assert.Null(runtime.Current.SideloadDirectory);
    }

    [Fact]
    public void EnsureBundledPackageInstalled_WithoutPackageSource_LeavesTheHostNotInstalled()
    {
        var runtime = CreateBrowserRuntime(Path.Combine(_root, "CollectorPackages", "Browser"));

        var snapshot = runtime.EnsureBundledPackageInstalled();

        Assert.False(snapshot.IsInstalled);
        Assert.Equal(BrowserCollectorRuntimeStatus.Waiting, snapshot.RuntimeStatus);
    }

    [Fact]
    public void CorruptPackageSource_DegradesBrowserInsteadOfFailingTheHost()
    {
        var source = Path.Combine(_root, "CollectorPackages", "Browser");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "collector-manifest.json"), "{ not json");

        var runtime = CreateBrowserRuntime(source);
        var snapshot = runtime.EnsureBundledPackageInstalled();

        Assert.False(snapshot.IsInstalled);
        Assert.Equal(BrowserCollectorRuntimeStatus.Degraded, snapshot.RuntimeStatus);
        Assert.NotEmpty(snapshot.RuntimeStatusDetail);
    }

    [Fact]
    public void CorruptInstallationLedger_DegradesBrowserInsteadOfFailingTheHost()
    {
        File.WriteAllText(Path.Combine(_root, "browser-package-state.json"), "{ not json");

        var runtime = CreateBrowserRuntime(packageDirectory: null);

        Assert.False(runtime.Current.IsInstalled);
        Assert.Equal(BrowserCollectorRuntimeStatus.Degraded, runtime.Current.RuntimeStatus);
    }

    [Fact]
    public async Task Hello_WithoutInstallation_RejectsOnlyThatConnection()
    {
        var options = new BrowserExternalHostBindingOptions(
            Path.Combine(_root, "CollectorPackages", "Browser"),
            TimeSpan.FromSeconds(10))
        {
            DataDirectory = _root
        };
        var browserRuntime = new BrowserCollectorRuntime(_runtime, new Device(), options);
        await using var handler = new BrowserExternalHostProtocolHandler(
            _runtime,
            new MutableRegistry(),
            browserRuntime,
            options,
            _time);

        var rejected = await Post(handler, "/v1/collector-protocol/browser/hello", HelloMessage());

        Assert.Equal(400, rejected.StatusCode);
        using var rejectedJson = JsonDocument.Parse(rejected.Body);
        Assert.Equal(
            "package_not_installed",
            rejectedJson.RootElement.GetProperty("body").GetProperty("error").GetProperty("code").GetString());

        // 同一 binding 在 Package 装好之后继续可用：被拒的只是那条连接，不是宿主。
        browserRuntime.Import(BrowserPackagePath);
        var accepted = await Post(handler, "/v1/collector-protocol/browser/hello", HelloMessage());
        Assert.Equal(200, accepted.StatusCode);
    }

    private BrowserCollectorRuntime CreateBrowserRuntime(string? packageDirectory) => new(
        _runtime,
        new Device(),
        new BrowserExternalHostBindingOptions(packageDirectory, TimeSpan.FromSeconds(10))
        {
            DataDirectory = _root
        });

    private static string BrowserPackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "BrowserCollectorPackage");

    private static string HelloMessage()
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            BrowserPackagePath,
            "browser-extension",
            "collector-artifact-ref.json")));
        var artifactHash = metadata.RootElement.GetProperty("artifactHash").GetString();
        var body = $$"""{"artifactId":"browser.extension","artifactHash":"{{artifactHash}}","protocolMajors":[1],"supportedCapabilities":{"facts.segment":[1],"diagnostics.stream-gap":[1]},"appHint":"edge","externalHostIdentity":"edge-profile-default"}""";
        return $$$"""
        {
          "protocol":"heartbeat.collector.bootstrap/1",
          "type":"activation.hello",
          "messageId":"{{{Guid.CreateVersion7()}}}",
          "body":{{{body}}}
        }
        """;
    }

    private static async Task<ProtocolHttpResponse> Post(
        BrowserExternalHostProtocolHandler handler,
        string path,
        string json)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return Assert.IsType<ProtocolHttpResponse>(await handler.HandleAsync("POST", path, body));
    }

    public void Dispose()
    {
        _runtime.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Device : IDeviceIdentity
    {
        public string HardwareId => "AAAAAAAA-BBBB-7CCC-8DDD-EEEEEEEEEEEE";
        public string DeviceName => "test";
    }

    private sealed class Resolver : ICollectorAppHintResolver
    {
        public CollectorAppHintResolution Resolve(string appHint) =>
            appHint == "edge" ? CollectorAppHintResolution.Resolved("win:msedge") : CollectorAppHintResolution.Unknown;
    }

    private sealed class Clock(ManualTimeProvider time) : IClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }

    private sealed class MutableRegistry : ICollectorRegistry
    {
        private string? _declaration;
        private int? _declarationVersion;
        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot =>
            new Dictionary<string, CollectorRegistration>
            {
                ["browser"] = new(true, 30_000, _declaration, _declarationVersion)
            };
        public CollectorRegistration Touch(string source, int? flushPeriodMs = null) => Snapshot["browser"];
        public void Discover(IEnumerable<string> sources) { }
        public void StoreDeclaration(string source, string declarationJson, int version)
        {
            _declaration = declarationJson;
            _declarationVersion = version;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
