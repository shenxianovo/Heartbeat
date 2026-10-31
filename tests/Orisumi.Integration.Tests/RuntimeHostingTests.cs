using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Orisumi.Observer.Runtime;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeHostingTests
{
    [Test]
    [Property("caseId", "observer-installation")]
    public async Task UninstallReinstallKeepsIdentityAndDisabledStateSurvivesRestart()
    {
        using var directory = new RuntimeDirectory();
        using var http = new HttpClient(new RuntimeProtocolTests.OfflineHandler()) { BaseAddress = new("http://localhost/") };
        Guid identity;
        using (var store = new PackageStore(directory.Data, directory.Bundled, null, http))
        {
            await using var host = new RuntimeHost(store, http);
            await host.InitializeAsync(CancellationToken.None);
            await host.RequestAsync("setEnabled", ClientProtocol.Json(new { instanceId = store.Observers.Last(o => o.PackageId == "example").InstanceId, enabled = false }), CancellationToken.None);
            identity = store.Observers.Last(o => o.PackageId == "example").InstanceId;
            await Assert.That(identity.Version).IsEqualTo(7);
        }
        using (var store = new PackageStore(directory.Data, directory.Bundled, null, http))
        {
            await using var host = new RuntimeHost(store, http);
            await host.InitializeAsync(CancellationToken.None);
            await Assert.That(store.Observers.Last(o => o.PackageId == "example").Enabled).IsFalse();
            await host.RequestAsync("uninstall", ClientProtocol.Json(new { packageId = "example" }), CancellationToken.None);
            await Assert.That(Directory.Exists(store.PackageDirectory("example", "0.1.0"))).IsFalse();
        }
        using (var store = new PackageStore(directory.Data, directory.Bundled, null, http))
        {
            await using var host = new RuntimeHost(store, http);
            await host.InitializeAsync(CancellationToken.None);
            await Assert.That(store.Observers.Last(o => o.PackageId == "example").Installed).IsFalse();
            await host.RequestAsync("install", ClientProtocol.Json(new { packageId = "example" }), CancellationToken.None);
            await Assert.That(store.Observers.Last(o => o.PackageId == "example").InstanceId).IsEqualTo(identity);
            await Assert.That(store.Observers.Last(o => o.PackageId == "example").Enabled).IsTrue();
        }
    }

    [Test]
    [Property("caseId", "observer-installation")]
    public async Task BadDigestAndArchiveTraversalDoNotChangeInstalledVersion()
    {
        using var directory = new RuntimeDirectory();
        using var http = new HttpClient(new RuntimeProtocolTests.OfflineHandler());
        using var store = new PackageStore(directory.Data, directory.Bundled, null, http);
        var original = store.Bundled().Single();
        store.Register(await store.PrepareAsync(original, CancellationToken.None), false);
        var bad = original with { Sha256 = new string('0', 64) };
        await Assert.That(async () => await store.PrepareAsync(bad, CancellationToken.None)).Throws<InvalidDataException>();
        var archive = Path.Combine(directory.Root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../../escaped").Open());
            writer.Write("outside");
        }
        using var stream = File.OpenRead(archive);
        var malicious = original with { Url = new Uri(archive).AbsoluteUri, Sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
        await Assert.That(async () => await store.PrepareAsync(malicious, CancellationToken.None)).Throws<InvalidDataException>();
        await Assert.That(File.Exists(Path.Combine(directory.Root, "escaped"))).IsFalse();
        await Assert.That(store.Observers.Last(o => o.PackageId == "example").Version).IsEqualTo("0.1.0");
    }

    [Test]
    [Property("caseId", "sdk-languages")]
    public async Task PythonObserverUsesHttpAndWritesBeforeExitingOnStdinClosure()
    {
        using var directory = new RuntimeDirectory();
        using var http = new HttpClient(new RuntimeProtocolTests.OfflineHandler()) { BaseAddress = new("http://localhost/") };
        using var store = new PackageStore(directory.Data, directory.Bundled, null, http);
        await using var host = new RuntimeHost(store, http);
        await host.InitializeAsync(CancellationToken.None);
        JsonElement observer;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        do
        {
            var list = await host.RequestAsync("list", ClientProtocol.Json(new { }), timeout.Token);
            observer = list.GetProperty("observers")[0];
            if (observer.GetProperty("state").GetString() == "running") break;
            await Task.Delay(50, timeout.Token);
        } while (true);
        await host.RequestAsync("setEnabled", ClientProtocol.Json(new { instanceId = store.Observers.Last(o => o.PackageId == "example").InstanceId, enabled = false }), timeout.Token);
        var stopped = (await host.RequestAsync("list", ClientProtocol.Json(new { }), timeout.Token)).GetProperty("observers")[0];
        await Assert.That(stopped.GetProperty("state").GetString()).IsEqualTo("stopped");
        // Unpaired collection is durably accepted by the local HTTP endpoint.
        var replies = await File.ReadAllTextAsync(Path.Combine(store.InstanceDirectory(store.Observers.Last(o => o.PackageId == "example").InstanceId), "data", "stopped"));
        await Assert.That(replies).IsEqualTo("202,202");
    }

    [Test]
    [Property("caseId", "observer-installation")]
    public async Task RuntimeDirectoryHasOneOwnerAndCorruptRegistryIsNotReplaced()
    {
        using var directory = new RuntimeDirectory();
        using var http = new HttpClient();
        using (var first = new PackageStore(directory.Data, null, null, http))
            await Assert.That(() => new PackageStore(directory.Data, null, null, http)).Throws<IOException>();
        var registry = Path.Combine(directory.Data, "registry.json");
        await File.WriteAllTextAsync(registry, "{");
        await Assert.That(() => new PackageStore(directory.Data, null, null, http)).Throws<JsonException>();
        await Assert.That(await File.ReadAllTextAsync(registry)).IsEqualTo("{");
    }

    [Test]
    [Property("caseId", "observer-lifecycle")]
    public async Task RepeatedCrashStopsAfterThreeRetriesAndDisabledStateCancelsRecovery()
    {
        const string script = """
            #!/usr/bin/env python3
            import os, sys, pathlib
            count = pathlib.Path(os.environ['ORISUMI_DATA_DIR'], 'attempts')
            count.write_text(str(int(count.read_text()) + 1) if count.exists() else '1')
            sys.exit(7)
            """;
        using var directory = new RuntimeDirectory(script);
        using var http = new HttpClient(new RuntimeProtocolTests.OfflineHandler()) { BaseAddress = new("http://localhost/") };
        using var packages = new PackageStore(directory.Data, directory.Bundled, null, http);
        await using var host = new RuntimeHost(packages, http);
        await host.InitializeAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        while (true)
        {
            var current = (await host.RequestAsync("list", ClientProtocol.Json(new { }), timeout.Token)).GetProperty("observers")[0];
            if (current.GetProperty("state").GetString() == "failed") break;
            await Task.Delay(50, timeout.Token);
        }
        var count = Path.Combine(packages.InstanceDirectory(packages.Observers.Last(o => o.PackageId == "example").InstanceId), "data", "attempts");
        await Assert.That(await File.ReadAllTextAsync(count)).IsEqualTo("4");
        await host.RequestAsync("retry", ClientProtocol.Json(new { instanceId = packages.Observers.Last(o => o.PackageId == "example").InstanceId }), timeout.Token);
        await host.RequestAsync("setEnabled", ClientProtocol.Json(new { instanceId = packages.Observers.Last(o => o.PackageId == "example").InstanceId, enabled = false }), timeout.Token);
        var before = await File.ReadAllTextAsync(count);
        await Task.Delay(1200, timeout.Token);
        await Assert.That(await File.ReadAllTextAsync(count)).IsEqualTo(before);
        var final = (await host.RequestAsync("list", ClientProtocol.Json(new { }), timeout.Token)).GetProperty("observers")[0];
        await Assert.That(final.GetProperty("enabled").GetBoolean()).IsFalse();
        await Assert.That(final.GetProperty("state").GetString()).IsEqualTo("stopped");
    }

    [Test]
    [Property("caseId", "observer-updates")]
    public async Task PreparedUpdateKeepsIdentitySettingsAndDisabledIntent()
    {
        using var directory = new RuntimeDirectory();
        using var http = new HttpClient(new RuntimeProtocolTests.OfflineHandler()) { BaseAddress = new("http://localhost/") };
        using var packages = new PackageStore(directory.Data, directory.Bundled, null, http);
        var manifest = await packages.PrepareAsync(packages.Bundled().Single(), CancellationToken.None);
        var initial = packages.Register(manifest, false) with { Settings = ClientProtocol.Json(new { intervalMs = 500 }) };
        packages.Save(initial);
        using (var zip = ZipFile.Open(Path.Combine(directory.Bundled, "update.zip"), ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("observer.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { format = 1, id = "example", name = "Updated", version = "0.2.0", target = PackageStore.Target, protocol = 1, entry = "bin/observer" }));
            using var executable = new StreamWriter(zip.CreateEntry("bin/observer").Open());
            executable.Write("#!/bin/sh\nexit 1\n");
        }
        await using var host = new RuntimeHost(packages, http);
        await host.InitializeAsync(CancellationToken.None);
        await host.RequestAsync("update", ClientProtocol.Json(new { packageId = "example" }), CancellationToken.None);
        var updated = packages.Observers.Last(o => o.PackageId == "example");
        await Assert.That(updated.InstanceId).IsEqualTo(initial.InstanceId);
        await Assert.That(updated.Settings.GetProperty("intervalMs").GetInt32()).IsEqualTo(500);
        await Assert.That(updated.Enabled).IsFalse();
        await Assert.That(updated.Version).IsEqualTo("0.2.0");
        var status = (await host.RequestAsync("list", ClientProtocol.Json(new { }), CancellationToken.None)).GetProperty("observers")[0];
        await Assert.That(status.GetProperty("state").GetString()).IsEqualTo("stopped");
    }

    [Test, Property("caseId", "service-runtime")]
    public async Task DeviceIdentityIsStableForThisMachine()
    {
        var read = DeviceIdentity.Read();
        await Assert.That(read).IsEqualTo(DeviceIdentity.Read());
        if (!OperatingSystem.IsMacOS()) return;
        var device = JsonSerializer.Deserialize<Orisumi.Observer.Sdk.DeviceContext>(read!, Orisumi.Observer.Sdk.DataClient.Json)!;
        await Assert.That(device.Key.Scheme).IsEqualTo("macos.hardware-uuid");
        await Assert.That(Guid.TryParse(device.Key.Value, out _)).IsTrue();
        await Assert.That(device.Platform).IsEqualTo("macos");
    }

    [Test, Property("caseId", "observer-lifecycle")]
    public async Task ShellObserverReceivesContextAndStopsWhenStdinCloses()
    {
        const string script = """
            #!/bin/sh
            printf '%s\n' "$ORISUMI_INSTANCE_ID" "$ORISUMI_DEVICE" "$ORISUMI_WRITE_URL" "$ORISUMI_API_URL" "$ORISUMI_SETTINGS" > "$ORISUMI_DATA_DIR/context"
            echo 'shell diagnostic' >&2
            cat > /dev/null
            echo stopped > "$ORISUMI_DATA_DIR/stopped"
            """;
        using var directory = new RuntimeDirectory(script);
        using var http = new HttpClient(new RuntimeProtocolTests.OfflineHandler()) { BaseAddress = new("http://localhost/") };
        using var packages = new PackageStore(directory.Data, directory.Bundled, null, http);
        var device = """{"key":{"scope":null,"scheme":"test.device","value":"one"},"name":"Mac","platform":"macos"}""";
        await using var host = new RuntimeHost(packages, http, device: device);
        await host.InitializeAsync(CancellationToken.None);
        var instance = packages.Observers.Single().InstanceId;
        var data = Path.Combine(packages.InstanceDirectory(instance), "data");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(Path.Combine(data, "context"))) await Task.Delay(25, timeout.Token);
        var lines = await File.ReadAllLinesAsync(Path.Combine(data, "context"), timeout.Token);
        await Assert.That(lines[0]).IsEqualTo(instance.ToString());
        await Assert.That(lines[1]).IsEqualTo(device);
        await Assert.That(lines[2].EndsWith($"/instances/{instance}/", StringComparison.Ordinal)).IsTrue();
        var connection = await host.RequestAsync("connection", ClientProtocol.Json(new { }), timeout.Token);
        await Assert.That(connection.GetProperty("api").GetProperty("credential").ValueKind).IsEqualTo(JsonValueKind.Null);
        await host.RequestAsync("setEnabled", ClientProtocol.Json(new { instanceId = instance, enabled = false }), timeout.Token);
        await Assert.That((await File.ReadAllTextAsync(Path.Combine(data, "stopped"), timeout.Token)).Trim()).IsEqualTo("stopped");
    }

    [Test, Property("caseId", "observer-installation")]
    public async Task InstallFromUrlWithoutDigestReturnsPackageAndInstance()
    {
        using var directory = new RuntimeDirectory();
        using var download = new HttpClient(new ArchiveHandler(await File.ReadAllBytesAsync(Path.Combine(directory.Bundled, "example.zip"))));
        using var backend = new HttpClient(new RuntimeProtocolTests.OfflineHandler()) { BaseAddress = new("http://localhost/") };
        using var packages = new PackageStore(directory.Data, null, null, download);
        await using var host = new RuntimeHost(packages, backend);
        await host.InitializeAsync(CancellationToken.None);
        var result = await host.RequestAsync("install", ClientProtocol.Json(new { url = "http://localhost/example.zip" }), CancellationToken.None);
        await Assert.That(result.GetProperty("packageId").GetString()).IsEqualTo("example");
        var id = result.GetProperty("instanceId").GetGuid();
        await Assert.That(id.Version).IsEqualTo(7);
        await Assert.That(packages.Find(id).Installed).IsTrue();
    }
    private sealed class ArchiveHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    internal sealed class RuntimeDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("orisumi-runtime-");
        public string Root => _directory.FullName;
        public string Data => Path.Combine(Root, "state");
        public string Bundled => Path.Combine(Root, "bundled");
        public RuntimeDirectory(string? script = null)
        {
            Directory.CreateDirectory(Bundled);
            using var zip = ZipFile.Open(Path.Combine(Bundled, "example.zip"), ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("observer.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { format = 1, id = "example", name = "Example", version = "0.1.0", target = PackageStore.Target, protocol = 1, entry = "bin/observer" }));
            using (var writer = new StreamWriter(zip.CreateEntry("bin/observer").Open())) writer.Write(script ?? PythonObserver);
        }
        public void Dispose() => _directory.Delete(true);
        private const string PythonObserver = """
            #!/usr/bin/env python3
            import json, sys, os, pathlib, urllib.request
            def write():
                body = json.dumps({'observer': {'entityId': os.environ['ORISUMI_INSTANCE_ID']}, 'writes': []}).encode()
                req = urllib.request.Request(os.environ['ORISUMI_WRITE_URL'] + 'writes', data=body, headers={'Content-Type': 'application/json'})
                return str(urllib.request.urlopen(req).status)
            replies = [write()]
            sys.stdin.read()
            replies.append(write())
            pathlib.Path(os.environ['ORISUMI_DATA_DIR'], 'stopped').write_text(','.join(replies))
            """;
    }
}
