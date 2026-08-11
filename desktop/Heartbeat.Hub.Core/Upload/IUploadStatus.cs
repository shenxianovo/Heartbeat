namespace Heartbeat.Hub.Core.Upload;

/// <summary>Read-only presentation seam for all configured Upload Streams.</summary>
public interface IUploadStatus
{
    IReadOnlyDictionary<string, UploadStreamStatus> Snapshot { get; }
    event Action? Changed;
}

public sealed class UploadStatusRegistry : IUploadStatus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UploadStreamStatus> _streams =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, UploadStreamStatus> Snapshot
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, UploadStreamStatus>(_streams, StringComparer.OrdinalIgnoreCase);
        }
    }

    public event Action? Changed;

    public void Update(string stream, UploadStreamStatus status)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var current) || current != status)
            {
                _streams[stream] = status;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }
}
