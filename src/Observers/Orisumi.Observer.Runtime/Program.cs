using System.CommandLine;

namespace Orisumi.Observer.Runtime;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var data = new Option<string>("--data-dir") { Required = true, Description = "Runtime identity, packages and durable data directory." };
        var backend = new Option<string>("--backend") { DefaultValueFactory = _ => "http://localhost:8080/api/" };
        var bundled = new Option<string?>("--bundled");
        var catalog = new Option<string?>("--catalog");
        var run = new Option<string?>("--run") { Description = "Run an installed Observer without a Client until interrupted." };
        var command = new RootCommand("Orisumi Observer Runtime") { data, backend, bundled, catalog, run };
        command.SetAction(async (parsed, token) =>
        {
            var address = new Uri(parsed.GetValue(backend)!.TrimEnd('/') + "/", UriKind.Absolute);
            if (address.Scheme is not ("http" or "https")) throw new ArgumentException("backend must be an HTTP address.");
            using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
            using var downloads = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var catalogUrl = parsed.GetValue(catalog);
            using var packages = new PackageStore(parsed.GetValue(data)!, parsed.GetValue(bundled), catalogUrl is null ? null : new Uri(catalogUrl), downloads);
            await using var host = new RuntimeHost(packages, http);
            await host.InitializeAsync(token, parsed.GetValue(run));
            if (parsed.GetValue(run) is { } package)
            {
                await host.RequestAsync("setEnabled", Sdk.JsonLines.Json(new { packageId = package, enabled = true }), token);
                string? reported = null;
                while (true)
                {
                    var list = await host.RequestAsync("list", Sdk.JsonLines.Json(new { }), token);
                    var status = list.GetProperty("observers").EnumerateArray().Single(item => item.GetProperty("packageId").GetString() == package);
                    var line = status.GetRawText();
                    if (line != reported) { await Console.Error.WriteLineAsync(line); reported = line; }
                    if (status.GetProperty("state").GetString() == "failed") return 1;
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }
            else await host.ServeAsync(Console.In, Console.Out, token);
            return 0;
        });
        try { return await command.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }); }
        catch (OperationCanceledException) { return 0; }
        catch (Exception exception) { await Console.Error.WriteLineAsync(exception.Message); return 1; }
    }
}
