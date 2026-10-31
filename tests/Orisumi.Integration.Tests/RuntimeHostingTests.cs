using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Orisumi.Observer.Runtime;
using Orisumi.Observer.Sdk;

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
            await host.RequestAsync("setEnabled", JsonLines.Json(new { packageId = "example", enabled = false }), CancellationToken.None);
            identity = store.Find("example").InstanceId;
            await Assert.That(identity.Version).IsEqualTo(7);
        }
        using (var store = new PackageStore(directory.Data, directory.Bundled, null, http))
        {
            await using var host = new RuntimeHost(store, http);
            await host.InitializeAsync(CancellationToken.None);
            await Assert.That(store.Find("example").Enabled).IsFalse();
            await host.RequestAsync("uninstall", JsonLines.Json(new { packageId = "example" }), CancellationToken.None);
            await Assert.That(Directory.Exists(store.PackageDirectory("example", "0.1.0"))).IsFalse();
        }
        using (var store = new PackageStore(directory.Data, directory.Bundled, null, http))
        {
            await using var host = new RuntimeHost(store, http);
            await host.InitializeAsync(CancellationToken.None);
            await Assert.That(store.Find("example").Installed).IsFalse();
            await host.RequestAsync("install", JsonLines.Json(new { packageId = "example" }), CancellationToken.None);
            await Assert.That(store.Find("example").InstanceId).IsEqualTo(identity);
            await Assert.That(store.Find("example").Enabled).IsTrue();
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
        await Assert.That(store.Find("example").Version).IsEqualTo("0.1.0");
    }

    [Test]
    [Property("caseId", "sdk-languages")]
    public async Task PythonObserverUsesTheSamePipeAndSubmitsBeforeExitingOnStop()
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
            var list = await host.RequestAsync("list", JsonLines.Json(new { }), timeout.Token);
            observer = list.GetProperty("observers")[0];
            if (observer.GetProperty("state").GetString() == "running") break;
            await Task.Delay(50, timeout.Token);
        } while (true);
        await host.RequestAsync("setEnabled", JsonLines.Json(new { packageId = "example", enabled = false }), timeout.Token);
        var stopped = (await host.RequestAsync("list", JsonLines.Json(new { }), timeout.Token)).GetProperty("observers")[0];
        await Assert.That(stopped.GetProperty("state").GetString()).IsEqualTo("stopped");
        // The host is not paired, so both commits reach the Runtime through the pipe and come back unavailable.
        var replies = await File.ReadAllTextAsync(Path.Combine(store.InstanceDirectory(store.Find("example").InstanceId), "data", "stopped"));
        await Assert.That(replies).IsEqualTo("unavailable,unavailable");
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
            import json, sys, pathlib
            init = json.loads(sys.stdin.readline())
            count = pathlib.Path(init['dataDirectory'], 'attempts')
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
            var current = (await host.RequestAsync("list", JsonLines.Json(new { }), timeout.Token)).GetProperty("observers")[0];
            if (current.GetProperty("state").GetString() == "failed") break;
            await Task.Delay(50, timeout.Token);
        }
        var count = Path.Combine(packages.InstanceDirectory(packages.Find("example").InstanceId), "data", "attempts");
        await Assert.That(await File.ReadAllTextAsync(count)).IsEqualTo("4");
        await host.RequestAsync("retry", JsonLines.Json(new { packageId = "example" }), timeout.Token);
        await host.RequestAsync("setEnabled", JsonLines.Json(new { packageId = "example", enabled = false }), timeout.Token);
        var before = await File.ReadAllTextAsync(count);
        await Task.Delay(1200, timeout.Token);
        await Assert.That(await File.ReadAllTextAsync(count)).IsEqualTo(before);
        var final = (await host.RequestAsync("list", JsonLines.Json(new { }), timeout.Token)).GetProperty("observers")[0];
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
        var initial = packages.Register(manifest, false) with { Settings = JsonLines.Json(new { intervalMs = 500 }) };
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
        await host.RequestAsync("update", JsonLines.Json(new { packageId = "example" }), CancellationToken.None);
        var updated = packages.Find("example");
        await Assert.That(updated.InstanceId).IsEqualTo(initial.InstanceId);
        await Assert.That(updated.Settings.GetProperty("intervalMs").GetInt32()).IsEqualTo(500);
        await Assert.That(updated.Enabled).IsFalse();
        await Assert.That(updated.Version).IsEqualTo("0.2.0");
        var status = (await host.RequestAsync("list", JsonLines.Json(new { }), CancellationToken.None)).GetProperty("observers")[0];
        await Assert.That(status.GetProperty("state").GetString()).IsEqualTo("stopped");
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
            import json, sys, uuid, pathlib, time
            init = json.loads(sys.stdin.readline())
            entity = str(uuid.UUID(int=(int(time.time()*1000)<<80)|(7<<76)|(2<<62)|(uuid.uuid4().int&((1<<62)-1))))
            def request(method, args):
                identity = str(uuid.UUID(int=(int(time.time()*1000)<<80)|(7<<76)|(2<<62)|(uuid.uuid4().int&((1<<62)-1))))
                print(json.dumps({'id':identity,'method':method,'params':args}), flush=True)
                reply = json.loads(sys.stdin.readline())
                return reply.get('error', {}).get('code', 'ok')
            def write(description):
                return request('commit', {'operations':[{'op':'write','entityId':entity,'category':'entity_schema','content':{'displayName':'Python example','description':description,'resourceName':'python-examples','fields':{}}}]})
            replies = [write('Started')]
            request('ready', {})
            for line in sys.stdin:
                if json.loads(line).get('event') == 'stop':
                    replies.append(write('Stopped'))
                    pathlib.Path(init['dataDirectory'], 'stopped').write_text(','.join(replies))
                    break
            """;
    }
}
