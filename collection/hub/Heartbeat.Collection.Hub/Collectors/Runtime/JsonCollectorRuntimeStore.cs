using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

internal sealed class JsonCollectorRuntimeStore : IDisposable
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _filePath;
    private readonly FileStream _ownershipLock;
    private readonly Action? _beforePrepare;

    public JsonCollectorRuntimeStore(string filePath, Action? beforePrepare = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _beforePrepare = beforePrepare;
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        try
        {
            _ownershipLock = new FileStream(
                _filePath + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CollectorRuntimeStateException(
                $"Collector Runtime state '{_filePath}' already has an owner or cannot be locked.",
                exception);
        }
    }

    public void Dispose() => _ownershipLock.Dispose();

    public CollectorRuntimeState Load()
    {
        if (!File.Exists(_filePath))
            return new CollectorRuntimeState();

        try
        {
            var state = Deserialize(File.ReadAllBytes(_filePath));
            Validate(state);
            return state;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new CollectorRuntimeStateException(
                $"Unable to load Collector Runtime state '{_filePath}'.",
                exception);
        }
    }

    private static CollectorRuntimeState Deserialize(byte[] bytes)
    {
        var root = JsonNode.Parse(bytes) as JsonObject
                   ?? throw new JsonException("Collector Runtime state must be a JSON object.");
        if (root["schemaVersion"] is not JsonValue schemaVersionNode ||
            !schemaVersionNode.TryGetValue<int>(out var schemaVersion))
            throw new JsonException("Collector Runtime state schemaVersion must be an integer.");
        if (root["instances"] is JsonArray currentInstances)
        {
            foreach (var instance in currentInstances.OfType<JsonObject>())
                instance.Remove("packageFingerprints");
        }
        if (schemaVersion == 1)
        {
            MoveLegacyProperty(root, "helloAttempts", "activationAttemptTombstones");
            if (root["instances"] is JsonArray instances)
            {
                foreach (var instance in instances.OfType<JsonObject>())
                {
                    MoveLegacyProperty(instance, "configSchemaVersion", "configVersion");
                    if (instance["lastKnownGoodPackage"] is JsonObject lastKnownGood)
                        MoveLegacyProperty(lastKnownGood, "configSchemaVersion", "configVersion");
                }
            }
            root["schemaVersion"] = CurrentSchemaVersion;
        }
        return root.Deserialize<CollectorRuntimeState>(SerializerOptions)
               ?? throw new JsonException("Collector Runtime state is null.");
    }

    private static void MoveLegacyProperty(JsonObject owner, string legacyName, string currentName)
    {
        if (!owner.TryGetPropertyValue(legacyName, out var legacyValue))
            return;
        if (owner.ContainsKey(currentName))
            throw new JsonException(
                $"Collector Runtime state contains both '{legacyName}' and '{currentName}'.");
        owner.Remove(legacyName);
        owner[currentName] = legacyValue?.DeepClone();
    }

    public void Save(CollectorRuntimeState state)
    {
        using var replacement = Prepare(state);
        replacement.Commit();
    }

    public PreparedReplacement Prepare(CollectorRuntimeState state)
    {
        _beforePrepare?.Invoke();
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Validate(state);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            var reloaded = JsonSerializer.Deserialize<CollectorRuntimeState>(
                File.ReadAllBytes(tempPath),
                SerializerOptions)
                ?? throw new JsonException("Collector Runtime replacement state is null.");
            Validate(reloaded);
            return new PreparedReplacement(tempPath, _filePath);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw new CollectorRuntimeStateException(
                $"Unable to persist Collector Runtime state '{_filePath}'.",
                exception);
        }
    }

    internal sealed class PreparedReplacement(string temporaryPath, string destinationPath) : IDisposable
    {
        private bool _committed;

        public void Commit()
        {
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
                _committed = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CollectorRuntimeStateException(
                    $"Unable to publish Collector Runtime state '{destinationPath}'.",
                    exception);
            }
        }

        public void Dispose()
        {
            if (!_committed && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void Validate(CollectorRuntimeState state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion)
            throw new JsonException(
                $"Unsupported Collector Runtime state schemaVersion {state.SchemaVersion}.");
        if (state.Instances is null || state.Streams is null || state.Facts is null ||
            state.Gaps is null || state.ActivationAttemptTombstones is null)
            throw new JsonException("Collector Runtime state collections must be present.");
        if (state.Instances.Any(instance => instance is null) ||
            state.Streams.Any(stream => stream is null) ||
            state.Facts.Any(fact => fact is null) ||
            state.Gaps.Any(gap => gap is null) ||
            state.ActivationAttemptTombstones.Any(attempt => attempt is null))
            throw new JsonException("Collector Runtime state collections must not contain null entries.");
        if (state.Instances.Select(instance => instance.CollectorInstanceId).Distinct().Count() != state.Instances.Count)
            throw new JsonException("Collector Runtime state contains duplicate Collector Instance IDs.");
        if (state.Instances
                .Where(instance => instance.InstanceKey is not null)
                .GroupBy(instance => (
                    instance.PackageId,
                    instance.SubjectId,
                    instance.SubjectKind,
                    instance.InstanceKey))
                .Any(group => group.Count() > 1))
            throw new JsonException("Collector Runtime state contains duplicate Collector Instance keys.");
        if (state.Streams.Select(stream => stream.StreamId).Distinct().Count() != state.Streams.Count)
            throw new JsonException("Collector Runtime state contains duplicate Fact Stream IDs.");
        if (state.ActivationAttemptTombstones
                .Select(attempt => (attempt.CollectorInstanceId, attempt.MessageId))
                .Distinct()
                .Count() != state.ActivationAttemptTombstones.Count)
            throw new JsonException("Collector Runtime state contains duplicate activation.hello attempts.");
        if (state.ActivationAttemptTombstones.Select(attempt => attempt.ActivationId).Distinct().Count() !=
            state.ActivationAttemptTombstones.Count)
            throw new JsonException("Collector Runtime state contains duplicate Collector Activation IDs.");
        if (state.Facts.Select(fact => (fact.StreamId, fact.FactId)).Distinct().Count() != state.Facts.Count)
            throw new JsonException("Collector Runtime state contains duplicate committed Fact identities.");
        foreach (var instance in state.Instances)
        {
            if (instance.CollectorInstanceId == Guid.Empty || instance.SubjectId == Guid.Empty ||
                !Enum.IsDefined(instance.SubjectKind) ||
                instance.InstanceKey is { } instanceKey &&
                (string.IsNullOrWhiteSpace(instanceKey) || instanceKey.Length > 200 || instanceKey != instanceKey.Trim()) ||
                string.IsNullOrWhiteSpace(instance.PackageId) || string.IsNullOrWhiteSpace(instance.PackageVersion) ||
                !IsSha256(instance.PackageContentHash) ||
                instance.SpecRevision is <= 0 or > 9_007_199_254_740_991 ||
                instance.ConfigVersion <= 0 || instance.Config.ValueKind == JsonValueKind.Undefined ||
                instance.LastKnownGoodPackage is { } lastKnownGood &&
                (string.IsNullOrWhiteSpace(lastKnownGood.PackageVersion) ||
                 !IsSha256(lastKnownGood.PackageContentHash) ||
                 string.IsNullOrWhiteSpace(lastKnownGood.ArtifactId) ||
                 !IsSha256(lastKnownGood.ArtifactContentHash) ||
                 lastKnownGood.ConfigVersion <= 0))
                throw new JsonException("Collector Runtime state contains an invalid Collector Instance.");
        }
        foreach (var stream in state.Streams)
        {
            if (stream.StreamId == Guid.Empty || stream.CollectorInstanceId == Guid.Empty ||
                stream.SubjectId == Guid.Empty || !Enum.IsDefined(stream.SubjectKind) ||
                !Enum.IsDefined(stream.FactKind) || string.IsNullOrWhiteSpace(stream.OutputId) ||
                string.IsNullOrWhiteSpace(stream.Source) || string.IsNullOrWhiteSpace(stream.SchemaId) ||
                stream.SchemaMajor <= 0 || stream.SchemaRevision <= 0 ||
                !IsSha256(stream.SchemaHash) || stream.SchemaCatalog is null ||
                stream.SchemaCatalog.Count == 0 ||
                stream.SchemaCatalog.Any(pair => pair.Key <= 0 || !IsSha256(pair.Value)) ||
                stream.SchemaDocuments is null ||
                stream.SchemaDocuments.Count != stream.SchemaCatalog.Count ||
                stream.SchemaDocuments.Any(pair =>
                    pair.Key <= 0 || pair.Value is null ||
                    !stream.SchemaCatalog.TryGetValue(pair.Key, out var expectedHash) ||
                    !IsSha256Of(pair.Value, expectedHash)) ||
                !stream.SchemaCatalog.TryGetValue(stream.SchemaRevision, out var currentSchemaHash) ||
                currentSchemaHash != stream.SchemaHash || stream.Dimensions is null ||
                stream.Dimensions.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
                throw new JsonException("Collector Runtime state contains an invalid Fact Stream.");
        }
        var conflictingSchemaIdentity = state.Streams
            .SelectMany(stream => stream.SchemaCatalog.Select(pair => new
            {
                stream.SchemaId,
                stream.SchemaMajor,
                SchemaRevision = pair.Key,
                Hash = pair.Value,
                Document = stream.SchemaDocuments[pair.Key]
            }))
            .GroupBy(item => (item.SchemaId, item.SchemaMajor, item.SchemaRevision))
            .FirstOrDefault(group =>
            {
                var first = group.First();
                return group.Skip(1).Any(item =>
                    item.Hash != first.Hash &&
                    !FactSchemaContent.SemanticallyEquals(item.Document, first.Document));
            });
        if (conflictingSchemaIdentity is not null)
            throw new JsonException("Collector Runtime state contains conflicting Fact Schema meanings.");
        if (state.Streams.Any(stream => state.Instances.All(
                instance => instance.CollectorInstanceId != stream.CollectorInstanceId)))
            throw new JsonException("Collector Runtime state contains a Fact Stream for an unknown Collector Instance.");
        if (state.Streams.Any(stream => state.Instances.All(instance =>
                instance.CollectorInstanceId != stream.CollectorInstanceId ||
                instance.SubjectId != stream.SubjectId || instance.SubjectKind != stream.SubjectKind)))
            throw new JsonException("Collector Runtime state contains a Fact Stream with a mismatched Subject binding.");
        foreach (var attempt in state.ActivationAttemptTombstones)
        {
            if (attempt.CollectorInstanceId == Guid.Empty || !IsUuidV7(attempt.MessageId) ||
                !IsUuidV7(attempt.ActivationId) || !IsSha256(attempt.RequestHash))
                throw new JsonException("Collector Runtime state contains an invalid activation.hello attempt.");
        }
        if (state.ActivationAttemptTombstones.Any(attempt => state.Instances.All(
                instance => instance.CollectorInstanceId != attempt.CollectorInstanceId)))
            throw new JsonException("Collector Runtime state contains an activation.hello attempt for an unknown Collector Instance.");
        foreach (var fact in state.Facts)
        {
            var stream = state.Streams.SingleOrDefault(candidate => candidate.StreamId == fact.StreamId);
            if (fact.StreamId == Guid.Empty || !IsUuidV7(fact.FactId) || fact.SchemaRevision <= 0 ||
                fact.Revision is <= 0 or > 9_007_199_254_740_991 || !Enum.IsDefined(fact.RecordState) ||
                !IsSha256(fact.ContentHash) || stream is null ||
                fact.ObservedAt is { Offset: var offset } && offset != TimeSpan.Zero ||
                fact.RecordState == FactRecordState.Present && fact.Payload is null ||
                fact.RecordState == FactRecordState.Retracted && fact.Payload is not null)
                throw new JsonException("Collector Runtime state contains an invalid committed Fact.");
            FactTime time = stream.FactKind switch
            {
                FactKind.Segment when fact.OccurredAt is null &&
                                      fact.End >= fact.Start &&
                                      fact.Start.Offset == TimeSpan.Zero &&
                                      fact.End.Offset == TimeSpan.Zero =>
                    new SegmentFactTime(fact.Start, fact.End, fact.IsFinal),
                FactKind.Event when fact.OccurredAt is { Offset: var eventOffset } occurredAt &&
                                    eventOffset == TimeSpan.Zero =>
                    new EventFactTime(occurredAt),
                _ => throw new JsonException("Collector Runtime state contains an invalid Fact time.")
            };

            var payload = fact.Payload ?? default;
            if (payload.ValueKind != JsonValueKind.Undefined &&
                FactCanonicalization.ValidateProtocolJson(payload) is not null)
                throw new JsonException("Collector Runtime state contains non-canonical Fact payload JSON.");
            string contentHash;
            try
            {
                contentHash = FactCanonicalization.ContentHash(new FactSubmission(
                    fact.StreamId,
                    fact.SchemaRevision,
                    fact.FactId,
                    fact.Revision,
                    fact.ObservedAt,
                    fact.RecordState,
                    time,
                    payload));
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or FormatException or OverflowException)
            {
                throw new JsonException("Collector Runtime state contains a non-canonical committed Fact.", exception);
            }
            if (contentHash != fact.ContentHash)
                throw new JsonException("Collector Runtime state committed Fact content hash does not match its content.");
        }
        if (state.Facts.Any(fact => state.Streams.All(stream => stream.StreamId != fact.StreamId)))
            throw new JsonException("Collector Runtime state contains a Fact for an unknown Fact Stream.");
        if (state.Facts.Any(fact => state.Streams.All(stream =>
                stream.StreamId != fact.StreamId ||
                !stream.SchemaCatalog.ContainsKey(fact.SchemaRevision))))
            throw new JsonException("Collector Runtime state contains a Fact with a mismatched Stream schema.");
        var identifiedGaps = state.Gaps.Where(gap => gap.GapId != Guid.Empty).ToArray();
        if (identifiedGaps.Select(gap => (gap.StreamId, gap.GapId)).Distinct().Count() != identifiedGaps.Length)
            throw new JsonException("Collector Runtime state contains duplicate Stream Gaps.");
        foreach (var gap in state.Gaps)
        {
            if (gap.StreamId == Guid.Empty ||
                gap.GapId != Guid.Empty && !IsUuidV7(gap.GapId) ||
                gap.End <= gap.Start || string.IsNullOrWhiteSpace(gap.Reason) ||
                gap.EstimatedFactsLost is <= 0)
                throw new JsonException("Collector Runtime state contains an invalid Stream Gap.");
        }
        if (state.Gaps.Any(gap => state.Streams.All(stream => stream.StreamId != gap.StreamId)))
            throw new JsonException("Collector Runtime state contains a Gap for an unknown Fact Stream.");
    }

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(char.IsAsciiHexDigitLower);

    private static bool IsSha256Of(byte[] content, string expected) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content)) == expected;

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return value != Guid.Empty && text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }
}

public sealed class CollectorRuntimeStateException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class CollectorRuntimeState
{
    public int SchemaVersion { get; init; } = 2;
    public List<CollectorInstanceState> Instances { get; init; } = [];
    public List<FactStreamState> Streams { get; init; } = [];
    public List<CommittedFactState> Facts { get; init; } = [];
    public List<CommittedGapState> Gaps { get; init; } = [];
    public List<ActivationAttemptTombstoneState> ActivationAttemptTombstones { get; init; } = [];

    public CollectorRuntimeState WithInstance(CollectorInstanceState instance) => new()
    {
        SchemaVersion = SchemaVersion,
        Instances = [.. Instances, instance],
        Streams = [.. Streams],
        Facts = [.. Facts],
        Gaps = [.. Gaps],
        ActivationAttemptTombstones = [.. ActivationAttemptTombstones]
    };

    public CollectorRuntimeState WithInstanceAndStreams(
        CollectorInstanceState instance,
        IEnumerable<FactStreamState> streams)
    {
        var replacements = streams.ToDictionary(stream => stream.StreamId);
        foreach (var replacement in replacements.Values)
        {
            var existing = Streams.SingleOrDefault(stream => stream.StreamId == replacement.StreamId);
            if (existing is not null && !SameStreamIdentity(existing, replacement))
                throw new InvalidOperationException(
                    $"Fact Stream ID '{replacement.StreamId}' cannot replace a different durable Stream identity.");
        }
        var nextStreams = Streams
            .Select(stream => replacements.TryGetValue(stream.StreamId, out var replacement)
                ? replacement
                : stream)
            .ToList();
        nextStreams.AddRange(replacements.Values.Where(replacement =>
            Streams.All(stream => stream.StreamId != replacement.StreamId)));
        return new CollectorRuntimeState
        {
            SchemaVersion = SchemaVersion,
            Instances = Instances
                .Select(existing => existing.CollectorInstanceId == instance.CollectorInstanceId
                    ? instance
                    : existing)
                .ToList(),
            Streams = nextStreams,
            Facts = [.. Facts],
            Gaps = [.. Gaps],
            ActivationAttemptTombstones = [.. ActivationAttemptTombstones]
        };
    }

    private static bool SameStreamIdentity(FactStreamState left, FactStreamState right) =>
        left.CollectorInstanceId == right.CollectorInstanceId &&
        left.SubjectId == right.SubjectId &&
        left.SubjectKind == right.SubjectKind &&
        left.OutputId == right.OutputId &&
        left.Source == right.Source &&
        left.FactKind == right.FactKind &&
        left.SchemaId == right.SchemaId &&
        left.SchemaMajor == right.SchemaMajor &&
        left.Dimensions.Count == right.Dimensions.Count &&
        left.Dimensions.All(pair =>
            right.Dimensions.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public CollectorRuntimeState WithFact(
        CommittedFactState fact,
        CommittedFactState? evictedFact = null) => new()
        {
            SchemaVersion = SchemaVersion,
            Instances = [.. Instances],
            Streams = [.. Streams],
            Facts = [.. Facts.Where(existing =>
            (existing.StreamId != fact.StreamId || existing.FactId != fact.FactId) &&
            (evictedFact is null ||
             existing.StreamId != evictedFact.StreamId || existing.FactId != evictedFact.FactId)), fact],
            Gaps = [.. Gaps],
            ActivationAttemptTombstones = [.. ActivationAttemptTombstones]
        };

    public CollectorRuntimeState WithGap(CommittedGapState gap) => new()
    {
        SchemaVersion = SchemaVersion,
        Instances = [.. Instances],
        Streams = [.. Streams],
        Facts = [.. Facts],
        Gaps = [.. Gaps, gap],
        ActivationAttemptTombstones = [.. ActivationAttemptTombstones]
    };

    public CollectorRuntimeState WithBoundGapIdentity(CommittedGapState gap, Guid gapId) => new()
    {
        SchemaVersion = SchemaVersion,
        Instances = [.. Instances],
        Streams = [.. Streams],
        Facts = [.. Facts],
        Gaps = Gaps.Select(existing => ReferenceEquals(existing, gap)
                ? new CommittedGapState
                {
                    StreamId = existing.StreamId,
                    GapId = gapId,
                    Start = existing.Start,
                    End = existing.End,
                    Reason = existing.Reason,
                    EstimatedFactsLost = existing.EstimatedFactsLost
                }
                : existing)
            .ToList(),
        ActivationAttemptTombstones = [.. ActivationAttemptTombstones]
    };

    public CollectorRuntimeState WithActivationAttemptTombstone(ActivationAttemptTombstoneState attempt) => new()
    {
        SchemaVersion = SchemaVersion,
        Instances = [.. Instances],
        Streams = [.. Streams],
        Facts = [.. Facts],
        Gaps = [.. Gaps],
        ActivationAttemptTombstones = [.. ActivationAttemptTombstones, attempt]
    };
}

internal sealed record CollectorInstanceState
{
    public Guid CollectorInstanceId { get; init; }
    public string? InstanceKey { get; init; }
    public string PackageId { get; init; } = string.Empty;
    public string PackageVersion { get; init; } = string.Empty;
    public string PackageContentHash { get; init; } = string.Empty;
    public Guid SubjectId { get; init; }
    public SubjectKind SubjectKind { get; init; }
    public long SpecRevision { get; init; }
    public int ConfigVersion { get; init; }
    public JsonElement Config { get; init; }
    public LastKnownGoodCollectorPackage? LastKnownGoodPackage { get; init; }
}

internal sealed class FactStreamState
{
    public Guid StreamId { get; init; }
    public Guid CollectorInstanceId { get; init; }
    public Guid SubjectId { get; init; }
    public SubjectKind SubjectKind { get; init; }
    public string OutputId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public FactKind FactKind { get; init; }
    public string SchemaId { get; init; } = string.Empty;
    public int SchemaMajor { get; init; }
    public int SchemaRevision { get; init; }
    public string SchemaHash { get; init; } = string.Empty;
    public Dictionary<int, string> SchemaCatalog { get; init; } = [];
    public Dictionary<int, byte[]> SchemaDocuments { get; init; } = [];
    public Dictionary<string, string> Dimensions { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class CommittedFactState
{
    public Guid StreamId { get; init; }
    public Guid FactId { get; init; }
    public int SchemaRevision { get; init; }
    public long Revision { get; init; }
    public FactRecordState RecordState { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public bool IsFinal { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public JsonElement? Payload { get; init; }
    public string ContentHash { get; init; } = string.Empty;
}

internal sealed class CommittedGapState
{
    public Guid StreamId { get; init; }
    public Guid GapId { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int? EstimatedFactsLost { get; init; }
}

internal sealed class ActivationAttemptTombstoneState
{
    public Guid CollectorInstanceId { get; init; }
    public Guid MessageId { get; init; }
    public string RequestHash { get; init; } = string.Empty;
    public Guid ActivationId { get; init; }
}
