using Heartbeat.Collector.Desktop;
using Heartbeat.Hub;

namespace Heartbeat.Desktop;

public sealed class DesktopRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _pendingOperations;
    private bool _initialized;
    private bool _disposed;
    private readonly DesktopProfile _profile;
    private readonly IDesktopPlatform _platform;
    private readonly HttpClient _http;
    private readonly Dictionary<ObservationCapability, CapabilityObservation> _capabilities = [];
    private ApiKeyTokenProvider? _tokens;
    private RecordOutbox? _queue;
    private HubDeliveryLoop? _delivery;
    private CancellationTokenSource? _deliveryStop;
    private Task? _deliveryTask;
    private IDesktopObservationSource? _source;
    private CancellationTokenSource? _collectionStop;
    private Task? _collectionTask;
    private string? _collectionError;

    public DesktopRuntime(DesktopProfile profile, IDesktopPlatform platform, HttpClient? http = null)
    {
        _profile = profile;
        _platform = platform;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2_097_152 };
        Settings = profile.ReadSettings();
    }

    public bool IsBusy => Volatile.Read(ref _pendingOperations) != 0;

    // Called by the application host once, independently of window visibility.
    public Task InitializeAsync() => RunAsync(async () =>
    {
        if (_initialized) return;
        _initialized = true;
        if (Settings is not null) await StartCoreAsync();
    });

    public Task ConfigureAsync(Uri backend, Uri auth, Uri web, string? apiKey, CancellationToken cancellationToken = default) =>
        RunAsync(() => ConfigureCoreAsync(backend, auth, web, apiKey, cancellationToken));

    public Task StartAsync() => RunAsync(StartCoreAsync);
    public Task StopCollectionAsync() => RunAsync(StopCollectionCoreAsync);

    private async Task RunAsync(Func<Task> operation, bool disposing = false)
    {
        Interlocked.Increment(ref _pendingOperations);
        await _operations.WaitAsync();
        try
        {
            if (_disposed && disposing) return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            await operation();
        }
        finally
        {
            Interlocked.Decrement(ref _pendingOperations);
            _operations.Release();
        }
    }

    public DesktopSettings? Settings { get; private set; }
    public string CollectorName => _platform.DisplayName;
    public bool IsCollecting => _collectionTask is { IsCompleted: false };
    public string? Error => _collectionError ?? _delivery?.LastError;
    public QueueStatus Queue => _queue?.Status() ?? new QueueStatus(0, 0);
    public IReadOnlyList<CapabilityObservation> Capabilities
    {
        get { lock (_capabilities) return _capabilities.Values.ToArray(); }
    }

    private async Task ConfigureCoreAsync(Uri backend, Uri auth, Uri web, string? apiKey, CancellationToken cancellationToken = default)
    {
        DesktopSettings.RequireOrigin(backend);
        DesktopSettings.RequireOrigin(auth);
        DesktopSettings.RequireOrigin(web);
        var key = string.IsNullOrWhiteSpace(apiKey) ? _profile.ReadApiKey() : apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("请输入 API key。");
        using var provider = new ApiKeyTokenProvider(_http, auth, key);
        var token = await provider.GetTokenAsync(cancellationToken)
            ?? throw new InvalidOperationException($"无法验证 API key：{provider.LastError}");
        var settings = new DesktopSettings(backend, auth, web, token.OwnerId, Settings?.Target ?? _platform.GetTarget());
        try { await Task.Run(() => _profile.Save(settings, key), cancellationToken); }
        catch (IOException exception)
        {
            throw new IOException($"账号验证已通过，但保存连接失败：{exception.Message}", exception);
        }
        var collecting = IsCollecting;
        var delivering = _deliveryTask is not null;
        await StopCollectionCoreAsync();
        await StopHubAsync();
        Settings = settings;
        if (collecting) await StartCoreAsync();
        else if (delivering) EnsureHub(settings);
    }

    private async Task StartCoreAsync()
    {
        if (IsCollecting) return;
        await StopCollectionCoreAsync();
        var settings = Settings ?? throw new InvalidOperationException("请先在连接设置中接入后端。");
        EnsureHub(settings);
        _collectionError = null;
        lock (_capabilities) _capabilities.Clear();
        _source = _platform.CreateObservationSource();
        _source.Observation += OnObservation;
        _collectionStop = new CancellationTokenSource();
        var session = new DesktopCollectorSession(_platform.CollectorKey, _source,
            new LocalHubSubmissionClient(_queue!), _platform.Clock);
        var options = new DesktopCollectionOptions(settings.Target, Environment.MachineName,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(1500));
        _collectionTask = CollectAsync(session, options, _collectionStop.Token);
    }

    private void EnsureHub(DesktopSettings settings)
    {
        if (_deliveryTask is not null) return;
        var key = _profile.ReadApiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("系统凭据库中没有 API key，请重新保存连接。");
        _queue = new RecordOutbox(_profile.DatabasePath, settings.Destination);
        _tokens = new ApiKeyTokenProvider(_http, settings.AuthUrl, key);
        _delivery = new HubDeliveryLoop(new RecordUploader(_queue, _http, _tokens), TimeSpan.FromSeconds(5));
        _deliveryStop = new CancellationTokenSource();
        _deliveryTask = _delivery.RunAsync(_deliveryStop.Token);
    }

    private async Task CollectAsync(DesktopCollectorSession session, DesktopCollectionOptions options, CancellationToken token)
    {
        try { await session.RunAsync(options, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { _collectionError = "采集已停止，部分数据可能尚未交给 Hub。请检查本地磁盘和系统权限后重试。"; }
    }

    private void OnObservation(DesktopObservation observation)
    {
        if (observation is not DesktopObservation.Capability capability) return;
        lock (_capabilities) _capabilities[capability.Value.Capability] = capability.Value;
    }

    private async Task StopCollectionCoreAsync()
    {
        if (_collectionStop is null) return;
        await _collectionStop.CancelAsync();
        try { if (_collectionTask is not null) await _collectionTask; }
        finally
        {
            if (_source is not null) { _source.Observation -= OnObservation; _source.Dispose(); }
            _source = null;
            _collectionTask = null;
            _collectionStop.Dispose();
            _collectionStop = null;
        }
    }

    private async Task StopHubAsync()
    {
        if (_deliveryStop is not null)
        {
            await _deliveryStop.CancelAsync();
            try { if (_deliveryTask is not null) await _deliveryTask; }
            catch (OperationCanceledException) { }
            _deliveryStop.Dispose();
        }
        _deliveryStop = null;
        _deliveryTask = null;
        _delivery = null;
        _tokens?.Dispose();
        _tokens = null;
    }

    public ValueTask DisposeAsync() => new(RunAsync(DisposeCoreAsync, disposing: true));

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        try { await StopCollectionCoreAsync(); }
        finally
        {
            try { await StopHubAsync(); }
            finally { _http.Dispose(); _profile.Dispose(); }
        }
    }
}
