using System.Net.Http.Headers;
using Heartbeat.Collector.Sample;
using Heartbeat.Hub;

var address = Environment.GetEnvironmentVariable("HEARTBEAT_HUB_URL") ?? "http://127.0.0.1:4318";
var secret = Environment.GetEnvironmentVariable("HEARTBEAT_HUB_TOKEN");
var target = Environment.GetEnvironmentVariable("HEARTBEAT_SAMPLE_TARGET");
if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
    string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(target) || target.Trim().Length > 255)
{
    Console.Error.WriteLine("设置 HEARTBEAT_HUB_URL、HEARTBEAT_HUB_TOKEN 和稳定的 HEARTBEAT_SAMPLE_TARGET。详见 Hub Client README。");
    return 2;
}

using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
{
    BaseAddress = uri,
    Timeout = TimeSpan.FromSeconds(2),
};
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler onCancel = (_, args) => { args.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += onCancel;
try
{
    return await SampleCollector.RunAsync(target.Trim(), new HubSubmissionClient(http),
        Console.In, Console.Out, TimeProvider.System, stop.Token);
}
finally
{
    Console.CancelKeyPress -= onCancel;
}
