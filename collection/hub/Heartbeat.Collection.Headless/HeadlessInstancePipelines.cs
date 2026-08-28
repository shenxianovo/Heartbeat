using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Headless;

internal interface IHeadlessSegmentUpload : IDisposable
{
    Task<ApiResult> SendAsync(
        Guid collectorInstanceId,
        HeadlessManagedInstanceOptions instance,
        List<ActivitySegmentItem> batch);
}

/// <summary>
/// Owns every per-Instance projection, current-activity view, durable upload stream and local
/// storage lifecycle. The Fleet only registers Instances, reads their status and drains the module.
/// </summary>
internal sealed class HeadlessInstancePipelines(
    string dataDirectory,
    IHeadlessSegmentUpload segmentUpload) :
    ISegmentSink,
    ISubjectSegmentProjectionSink,
    IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Pipeline> _pipelines = [];

    public void Add(Guid collectorInstanceId, HeadlessManagedInstanceOptions instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var directory = Path.Combine(dataDirectory, "instances", collectorInstanceId.ToString("D"));
        Directory.CreateDirectory(directory);
        var ingest = new SegmentIngestService(new SystemClock());
        var cache = new JsonFileCache<ActivitySegmentItem>(
            Path.Combine(directory, "segments-cache.json"),
            20_000,
            HeartbeatCacheFormats.SegmentVersion2(),
            HeartbeatCacheFormats.SegmentMigrations());
        var upload = new UploadStream<ActivitySegmentItem>(
            $"段/{instance.InstanceKey}",
            ingest,
            batch => segmentUpload.SendAsync(collectorInstanceId, instance, batch),
            cache,
            SnapshotCompaction.KeepLatest,
            new JsonDeadLetterStore<ActivitySegmentItem>(
                Path.Combine(directory, "segments-dead-letter.json")),
            new UploadStatusRegistry(),
            new ClientCompatibilityStatus());
        var pipeline = new Pipeline(ingest, cache, upload);
        lock (_gate)
        {
            if (!_pipelines.TryAdd(collectorInstanceId, pipeline))
            {
                pipeline.Dispose();
                throw new InvalidOperationException(
                    $"Collector Instance '{collectorInstanceId:D}' already has a projection pipeline.");
            }
        }
    }

    public HeadlessCurrentSubjectActivity? CurrentActivity(Guid collectorInstanceId) =>
        Required(collectorInstanceId).CurrentActivity;

    public async Task DrainAllAsync()
    {
        Pipeline[] pipelines;
        lock (_gate) pipelines = _pipelines.Values.ToArray();
        foreach (var pipeline in pipelines)
            await pipeline.DrainAsync().ConfigureAwait(false);
    }

    public void Push(List<ActivitySegmentItem> snapshots) =>
        throw new NotSupportedException("Multi-Subject projection requires Collector Instance context.");

    public void UpsertDurable(
        CollectorProjectionContext context,
        ActivitySegmentItem snapshot,
        long revision,
        bool isFinal) => Required(context.CollectorInstanceId).Upsert(snapshot, revision, isFinal);

    public void ReplayDurable(
        CollectorProjectionContext context,
        ActivitySegmentItem snapshot,
        long revision,
        bool isFinal) => Required(context.CollectorInstanceId).Replay(snapshot, revision, isFinal);

    public void RetractDurable(
        CollectorProjectionContext context,
        Guid segmentId,
        long revision) => Required(context.CollectorInstanceId).Retract(segmentId, revision);

    public void Dispose()
    {
        Pipeline[] pipelines;
        lock (_gate)
        {
            pipelines = _pipelines.Values.ToArray();
            _pipelines.Clear();
        }
        foreach (var pipeline in pipelines)
            pipeline.Dispose();
        segmentUpload.Dispose();
    }

    private Pipeline Required(Guid collectorInstanceId)
    {
        lock (_gate)
            return _pipelines.TryGetValue(collectorInstanceId, out var pipeline)
                ? pipeline
                : throw new KeyNotFoundException(
                    $"Collector Instance '{collectorInstanceId:D}' has no projection pipeline.");
    }

    private sealed class Pipeline(
        SegmentIngestService ingest,
        JsonFileCache<ActivitySegmentItem> cache,
        UploadStream<ActivitySegmentItem> upload) : IDisposable
    {
        private readonly object _gate = new();
        private HeadlessCurrentSubjectActivity? _current;
        private Guid? _currentSegmentId;

        public HeadlessCurrentSubjectActivity? CurrentActivity
        {
            get { lock (_gate) return _current; }
        }

        public void Upsert(ActivitySegmentItem item, long revision, bool isFinal)
        {
            ingest.UpsertDurable(item, revision);
            Observe(item, isFinal);
        }

        public void Replay(ActivitySegmentItem item, long revision, bool isFinal)
        {
            ingest.ReplayDurable(item, revision);
            Observe(item, isFinal);
        }

        public void Retract(Guid segmentId, long revision)
        {
            ingest.RetractDurable(segmentId, revision);
            lock (_gate)
            {
                if (_currentSegmentId != segmentId)
                    return;
                _current = null;
                _currentSegmentId = null;
            }
        }

        public async Task DrainAsync() =>
            _ = await upload.DrainAsync().ConfigureAwait(false);

        public void Dispose() => cache.Dispose();

        private void Observe(ActivitySegmentItem item, bool isFinal)
        {
            lock (_gate)
            {
                if (_current is not null && item.StartTime < _current.StartTime)
                    return;
                if (isFinal)
                {
                    if (_currentSegmentId == item.Id)
                    {
                        _current = null;
                        _currentSegmentId = null;
                    }
                    return;
                }
                _current = new HeadlessCurrentSubjectActivity(
                    item.Title,
                    item.IdentityKey,
                    item.StartTime,
                    item.EndTime,
                    item.Attributes);
                _currentSegmentId = item.Id;
            }
        }
    }
}

internal sealed class HeadlessAnalyticsSegmentUploadAdapter(TokenManager tokens) : IHeadlessSegmentUpload
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HttpClient> _clients = [];

    public Task<ApiResult> SendAsync(
        Guid collectorInstanceId,
        HeadlessManagedInstanceOptions instance,
        List<ActivitySegmentItem> batch)
    {
        HttpClient http;
        lock (_gate)
        {
            if (!_clients.TryGetValue(collectorInstanceId, out http!))
            {
                var identity = new FixedSubjectIdentity(
                    $"subject:{instance.SubjectKind.ToString().ToLowerInvariant()}:{instance.SubjectId:D}",
                    instance.SubjectName);
                var handler = new BearerTokenHandler(tokens, identity)
                {
                    InnerHandler = new HttpClientHandler()
                };
                http = new HttpClient(handler, disposeHandler: true);
                _clients.Add(collectorInstanceId, http);
            }
        }
        return new HeartbeatApiClient(http)
            .UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch });
    }

    public void Dispose()
    {
        HttpClient[] clients;
        lock (_gate)
        {
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }
        foreach (var client in clients)
            client.Dispose();
    }

    private sealed record FixedSubjectIdentity(
        string HardwareId,
        string DeviceName) : IDeviceIdentity;
}
