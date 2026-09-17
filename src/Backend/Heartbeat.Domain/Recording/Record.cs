using System.Text.Json;

namespace Heartbeat.Recording;

/// <summary>
/// 一份观测结果。Record 只有创建路径，没有修改已存内容或时间的方法：ADR-0006 接受了结果更正的模型语义，
/// 机制尚未设计，见 docs/recording-open-questions.md 的「历史纠错与删除」。给这个类加 setter 或更新方法
/// 之前，先看那一条。
/// </summary>
public sealed class Record
{
    private Record()
    {
    }

    public Guid Id { get; private set; }

    public Guid TrackId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public DateTimeOffset? ObservedAt { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public JsonElement Value { get; private set; }

    public static Record Create(
        Guid id,
        Track track,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        DateTimeOffset? observedAt,
        DateTimeOffset receivedAt,
        JsonElement value)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A record ID is required.", nameof(id));
        }

        if (id.Version != 7)
        {
            throw new ArgumentException("A record ID must be a UUID v7.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(track);
        track.EnsureAccepts(endedAt);

        var normalizedStartedAt = startedAt.ToUniversalTime();
        var normalizedEndedAt = endedAt?.ToUniversalTime();
        var normalizedObservedAt = observedAt?.ToUniversalTime();

        if (normalizedEndedAt < normalizedStartedAt)
        {
            throw new ArgumentException("The end time cannot precede the start time.", nameof(endedAt));
        }

        return new Record
        {
            Id = id,
            TrackId = track.Id,
            StartedAt = normalizedStartedAt,
            EndedAt = normalizedEndedAt,
            ObservedAt = normalizedObservedAt == normalizedStartedAt ? null : normalizedObservedAt,
            ReceivedAt = receivedAt.ToUniversalTime(),
            Value = JsonValue.Clone(value, nameof(value)),
        };
    }
}
