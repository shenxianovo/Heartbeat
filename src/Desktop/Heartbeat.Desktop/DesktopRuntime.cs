using Heartbeat.Collector.Desktop;
using Heartbeat.Hub;

namespace Heartbeat.Desktop;

public sealed class DesktopRuntime : IAsyncDisposable
{
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

    public DesktopSettings? Settings { get; private set; }
    public string CollectorName => _platform.DisplayName;
    public bool IsCollecting => _collectionTask is { IsCompleted: false };
    public string? Error => _collectionError ?? _delivery?.LastError;
    public QueueStatus Queue => _queue?.Status() ?? new QueueStatus(0, 0);
    public IReadOnlyList<CapabilityObservation> Capabilities
    {
        get { lock (_capabilities) return _capabilities.Values.ToArray(); }
    }

    public async Task ConfigureAsync(Uri backend, Uri auth, Uri web, string? apiKey, CancellationToken cancellationToken = default)
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
        await StopCollectionAsync();
        await StopHubAsync();
        Settings = settings;
        if (collecting) await StartAsync();
        else if (delivering) EnsureHub(settings);
    }

    public async Task StartAsync()
    {
        if (IsCollecting) return;
        await StopCollectionAsync();
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

    public async Task StopCollectionAsync()
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

    public void OpenPermissionSettings(ObservationCapability capability) => _platform.OpenPermissionSettings(capability);

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

    public async ValueTask DisposeAsync()
    {
        try { await StopCollectionAsync(); }
        finally
        {
            try { await StopHubAsync(); }
            finally { _http.Dispose(); _profile.Dispose(); }
        }
    }
}
