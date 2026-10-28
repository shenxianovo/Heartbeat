using System.Diagnostics;
using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observer.Runtime;

internal sealed class ManagedObserver : IAsyncDisposable
{
    private readonly PackageStore _packages;
    private readonly LocalObserverSession _session;
    private readonly CancellationTokenSource _deliveryStop = new();
    private readonly Task _delivery;
    private CancellationTokenSource? _runStop;
    private Task? _run;
    private string _state = "stopped";
    private string? _error;
    public InstalledObserver Registration { get; private set; }
    public ObserverClient Sdk { get; }
    public string State => _state;
    public string? Error => _error;
    public string? Diagnostic { get; private set; }

    public ManagedObserver(PackageStore packages, InstalledObserver registration, HttpClient http)
    {
        _packages = packages;
        Registration = registration;
        var directory = packages.InstanceDirectory(registration.InstanceId);
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        Directory.CreateDirectory(Path.Combine(packages.Root, "shared"));
        Sdk = new ObserverClient(Path.Combine(directory, "sdk.sqlite"), new(registration.InstanceId), http) { PublishDrafts = false };
        _session = new(Sdk);
        _delivery = DeliverAsync(_deliveryStop.Token);
    }

    private async Task DeliverAsync(CancellationToken token)
    {
        try { await Sdk.RunAsync(TimeSpan.FromSeconds(2), cancellationToken: token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { _error = "Local delivery failed: " + exception.Message; }
    }

    public void Start()
    {
        if (!Registration.Installed || !Registration.Enabled || _run is { IsCompleted: false }) return;
        _runStop?.Dispose();
        _runStop = new();
        _error = null;
        _state = "starting";
        _run = Task.Run(() => RunAsync(_runStop.Token));
    }

    public async Task StopAsync()
    {
        if (_runStop is null || _run is null) return;
        await _runStop.CancelAsync();
        await _run;
    }

    public void SetRegistration(InstalledObserver value) => Registration = value;

    private async Task RunAsync(CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            TimeSpan running = TimeSpan.Zero;
            var incompatible = false;
            try { running = await RunProcessAsync(token); }
            catch (InvalidDataException exception) { _error = exception.Message; incompatible = true; }
            catch (Exception exception) { _error = exception.Message; }
            finally { Sdk.PublishDrafts = false; }
            if (token.IsCancellationRequested) { _state = "stopped"; return; }
            if (running >= TimeSpan.FromSeconds(60)) failures = 0;
            if (incompatible || failures >= 3) { _state = "failed"; return; }
            _state = "waiting";
            try { await Task.Delay(TimeSpan.FromSeconds(1 << failures++), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { _state = "stopped"; return; }
        }
        _state = "stopped";
    }

    private async Task<TimeSpan> RunProcessAsync(CancellationToken stopping)
    {
        var manifest = _packages.Manifest(Registration);
        if (manifest.Protocol != 1 || manifest.Format != 1) throw new InvalidDataException("Unsupported Observer protocol; update the Client.");
        var directory = _packages.PackageDirectory(Registration.PackageId, Registration.Version);
        var start = new ProcessStartInfo(PackageStore.ChildPath(directory, manifest.Entry))
        {
            WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not start Observer.");
        _state = "starting";
        using var writeGate = new SemaphoreSlim(1);
        using var connection = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long? readyAt = null;
        async Task WriteAsync(object message, CancellationToken token)
        {
            await writeGate.WaitAsync(token);
            try { await JsonLines.WriteAsync(process.StandardInput, JsonSerializer.Serialize(message, JsonSerializerOptions.Web), token); }
            finally { writeGate.Release(); }
        }
        var reading = Task.Run(async () =>
        {
            while (await JsonLines.ReadAsync(process.StandardOutput, connection.Token) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var request = json.RootElement;
                var id = request.GetProperty("id").GetString()!;
                try
                {
                    var method = request.GetProperty("method").GetString()!;
                    JsonElement result;
                    if (method == "ready")
                    {
                        if (!ready.TrySetResult()) throw new InvalidOperationException("Observer already reported ready.");
                        readyAt = Stopwatch.GetTimestamp();
                        _state = "running";
                        _error = null;
                        Sdk.PublishDrafts = true;
                        result = JsonLines.Json(new { });
                    }
                    else result = await _session.RequestAsync(method, request.GetProperty("params"), id, connection.Token);
                    await WriteAsync(new { id, result }, connection.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !connection.IsCancellationRequested)
                {
                    await WriteAsync(new { id, error = new { code = exception is HttpRequestException or OperationCanceledException ? "unavailable" : "invalid_request", message = exception.Message } }, connection.Token);
                }
            }
        }, CancellationToken.None);
        var diagnostics = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(connection.Token) is { } line)
                if (!string.IsNullOrWhiteSpace(line)) Diagnostic = line.Length <= 2048 ? line : line[..2048];
        }, CancellationToken.None);
        var exit = process.WaitForExitAsync(CancellationToken.None);
        var stop = Task.Delay(Timeout.InfiniteTimeSpan, stopping);
        try
        {
            await WriteAsync(new
            {
                @event = "init", protocol = 1, instanceId = Registration.InstanceId,
                dataDirectory = Path.Combine(_packages.InstanceDirectory(Registration.InstanceId), "data"),
                sharedDataDirectory = Path.Combine(_packages.Root, "shared"), packageVersion = Registration.Version, settings = Registration.Settings,
            }, connection.Token);
            var first = await Task.WhenAny(ready.Task, exit, reading, stop, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
            if (first != ready.Task && first != stop && !process.HasExited)
                _error = "Observer did not become ready within 10 seconds.";
            if (first == ready.Task) await Task.WhenAny(exit, reading, stop);
            if (!process.HasExited)
            {
                _state = "stopping";
                Sdk.PublishDrafts = false;
                if (stopping.IsCancellationRequested)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await WriteAsync(new { @event = "stop" }, timeout.Token); await exit.WaitAsync(timeout.Token); }
                    catch (Exception exception) when (exception is OperationCanceledException or IOException) { _error = "Observer did not stop within 10 seconds."; }
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            await exit;
            if (!stopping.IsCancellationRequested) _error ??= "Observer exited with code " + process.ExitCode + ".";
            return readyAt is { } timestamp ? Stopwatch.GetElapsedTime(timestamp) : TimeSpan.Zero;
        }
        finally
        {
            Sdk.PublishDrafts = false;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await connection.CancelAsync();
            try { await Task.WhenAll(reading, diagnostics); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or JsonException or InvalidOperationException or KeyNotFoundException) { _error ??= exception.Message; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _deliveryStop.CancelAsync();
        await _delivery;
        Sdk.Dispose();
        _runStop?.Dispose();
        _deliveryStop.Dispose();
    }
}
