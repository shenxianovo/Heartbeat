using System.CommandLine;
using System.Runtime.InteropServices;

namespace Orisumi.Observer.Runtime;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        { context.Cancel = true; shutdown.Cancel(); });
        var data = new Option<string>("--data-dir") { Required = true, Description = "Runtime identity, packages and durable data directory." };
        var backend = new Option<string>("--backend") { DefaultValueFactory = _ => "http://localhost:8080/api/" };
        var bundled = new Option<string?>("--bundled");
        var catalog = new Option<string?>("--catalog");
        var run = new Option<string?>("--run") { Description = "Run an installed Observer without a Client until interrupted." };
        var service = new Option<bool>("--service");
        var pairing = new Option<string?>("--pairing-code");
        var name = new Option<string>("--name") { DefaultValueFactory = _ => Environment.MachineName };
        var command = new RootCommand("Orisumi Observer Runtime") { data, backend, bundled, catalog, run, service, pairing, name };
        command.SetAction(async (parsed, invocationToken) =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(invocationToken, shutdown.Token);
            var token = lifetime.Token;
            var address = new Uri(parsed.GetValue(backend)!.TrimEnd('/') + "/", UriKind.Absolute);
            if (address.Scheme is not ("http" or "https") || !address.IsLoopback && address.Scheme != "https") throw new ArgumentException("backend must use HTTPS outside the local machine.");
            using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(35) };
            using var downloads = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var catalogUrl = parsed.GetValue(catalog);
            using var packages = new PackageStore(parsed.GetValue(data)!, parsed.GetValue(bundled), catalogUrl is null ? null : new Uri(catalogUrl), downloads);
            await using var host = new RuntimeHost(packages, http, parsed.GetValue(service), DeviceIdentity.Read());
            if (parsed.GetValue(pairing) is { } code) await host.PairAsync(code, parsed.GetValue(name)!, token);
            if (parsed.GetValue(service) && !host.Paired) throw new InvalidOperationException("Service mode requires a pairing code or an existing host identity.");
            await host.InitializeAsync(token, parsed.GetValue(run));
            host.StartSynchronization();
            if (parsed.GetValue(run) is { } package)
            {
                await host.RequestAsync("setEnabled", ClientProtocol.Json(new { instanceId = packages.Observers.Single(item => item.PackageId == package && !item.Removed).InstanceId, enabled = true }), token);
                string? reported = null;
                while (true)
                {
                    var list = await host.RequestAsync("list", ClientProtocol.Json(new { }), token);
                    var status = list.GetProperty("observers").EnumerateArray().Single(item => item.GetProperty("packageId").GetString() == package);
                    var line = status.GetRawText();
                    if (line != reported) { await Console.Error.WriteLineAsync(line); reported = line; }
                    if (status.GetProperty("state").GetString() == "failed") return 1;
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }
            else if (parsed.GetValue(service)) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            else await host.ServeAsync(Console.In, Console.Out, token);
            return 0;
        });
        try { return await command.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }); }
        catch (OperationCanceledException) { return 0; }
        catch (Exception exception) { await Console.Error.WriteLineAsync(exception.Message); return 1; }
    }
}
