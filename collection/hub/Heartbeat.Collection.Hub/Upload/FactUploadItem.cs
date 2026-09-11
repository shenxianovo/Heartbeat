using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Collection.Hub.Upload;

/// <summary>A transport snapshot. The Runtime retains custody until this exact content is confirmed.</summary>
public sealed record FactUploadItem(
    FactStreamDefinition? Stream,
    FactSnapshot? Fact,
    FactGapSnapshot? Gap,
    ObservationSnapshot? Observation = null,
    bool? IsFinal = null,
    DateTimeOffset? ObservedAt = null)
{
    public static ObservationUploadRequest ObservationRequest(IReadOnlyList<FactUploadItem> items) => new()
    {
        Facts = items.Where(item => item.Observation is not null).Select(item => item.Observation!).ToList()
    };

    public static FactUploadRequest Request(IReadOnlyList<FactUploadItem> items) => new()
    {
        Streams = items.Select(item => item.Stream).OfType<FactStreamDefinition>().DistinctBy(stream => stream.StreamId).ToList(),
        Facts = items.Where(item => item.Fact is not null).Select(item => item.Fact!).ToList(),
        Gaps = items.Where(item => item.Gap is not null).Select(item => item.Gap!).ToList()
    };
}

/// <summary>Optional, rebuildable host read model; never takes durable delivery responsibility.</summary>
public interface ICollectorFactObserver
{
    void Observe(FactUploadItem item);
}
