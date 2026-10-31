using System.Diagnostics;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Runtime.Forwarding;

namespace Orisumi.Observer.Runtime;

internal sealed class ManagedObserver : IAsyncDisposable
{
    private readonly PackageStore _packages;
    private readonly RuntimeConnection _connection;
    private readonly string _writeUrl;
    private readonly string? _device;
    public WriteQueue Writes { get; }
    private CancellationTokenSource? _runStop;
    private Task? _run;
    private string _state = "stopped";
    private string? _error;
    private bool _normalExit;
    public InstalledObserver Registration { get; private set; }
    public string State => Registration.SyncError is not null && Registration.Scope == "deployment" && Registration.Revision == 0 ? "failed" : _state;
    public string? Error => _error ?? (Registration.Scope == "deployment" && Registration.Revision == 0 ? Registration.SyncError ?? "Waiting for registration." : null);
    public string? Diagnostic { get; private set; }

    public ManagedObserver(PackageStore packages, InstalledObserver registration, RuntimeConnection connection, ForwardingServer forwarding, string? device)
    {
        _packages = packages; _connection = connection; _device = device;
        Registration = registration;
        var directory = packages.InstanceDirectory(registration.InstanceId);
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        Writes = new(directory, connection.Client, () => connection.CanConnect && Registration.Revision > 0);
        forwarding.Add(registration.InstanceId, Writes);
        _writeUrl = forwarding.InstanceAddress(registration.InstanceId);
    }

    public void Start()
    {
        if (Registration.Removed || (Registration.Scope == "deployment" && Registration.Revision == 0) || !Registration.Installed || !Registration.Enabled || _run is { IsCompleted: false }) return;
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
            _normalExit = false;
            try { running = await RunProcessAsync(token); }
            catch (InvalidDataException exception) { _error = exception.Message; incompatible = true; }
            catch (Exception exception) { _error = exception.Message; }
            if (token.IsCancellationRequested || _normalExit) { _state = "stopped"; return; }
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
        start.Environment["ORISUMI_INSTANCE_ID"] = Registration.InstanceId.ToString();
        start.Environment["ORISUMI_PACKAGE_VERSION"] = Registration.Version;
        start.Environment["ORISUMI_DATA_DIR"] = Path.Combine(_packages.InstanceDirectory(Registration.InstanceId), "data");
        start.Environment["ORISUMI_WRITE_URL"] = _writeUrl;
        start.Environment["ORISUMI_API_URL"] = _connection.Client.BaseAddress!.AbsoluteUri;
        start.Environment["ORISUMI_API_TOKEN"] = _connection.Identity?.Credential ?? "";
        start.Environment["ORISUMI_DEVICE"] = _device ?? "";
        start.Environment["ORISUMI_SETTINGS"] = Registration.Settings.GetRawText();
        using var process = Process.Start(start) ?? throw new IOException("Could not start Observer.");
        _state = "running"; _error = null;
        var started = Stopwatch.GetTimestamp();
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        var diagnostics = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) Diagnostic = line.Length <= 2048 ? line : line[..2048];
        }, CancellationToken.None);
        var exit = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            await Task.WhenAny(exit, Task.Delay(Timeout.InfiniteTimeSpan, stopping));
            if (!process.HasExited)
            {
                _state = "stopping";
                process.StandardInput.Close();
                try { await exit.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
                catch (TimeoutException) { _error = "Observer did not stop within 10 seconds."; process.Kill(entireProcessTree: true); }
            }
            await exit;
            _normalExit = process.ExitCode == 0;
            if (!stopping.IsCancellationRequested && !_normalExit) _error = "Observer exited with code " + process.ExitCode + ".";
            return Stopwatch.GetElapsedTime(started);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await exit; await Task.WhenAll(output, diagnostics);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _runStop?.Dispose();
        await Writes.DisposeAsync();
    }
}
