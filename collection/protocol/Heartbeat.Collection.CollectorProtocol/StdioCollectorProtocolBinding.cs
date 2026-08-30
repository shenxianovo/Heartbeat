using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Heartbeat.Collection.CollectorProtocol;

/// <summary>Newline-delimited JSON stdio adapter for a ManagedProcess Collector.</summary>
public sealed class StdioCollectorProtocolBinding : ICollectorProtocolBinding
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly CollectorRuntimeArtifact _artifact;
    private readonly Guid _collectorInstanceId;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JsonDocument>> _responses = new();
    private readonly Channel<JsonDocument> _authorizationResponses = Channel.CreateUnbounded<JsonDocument>();
    private readonly Channel<CollectorDrainRequest> _drains = Channel.CreateBounded<CollectorDrainRequest>(1);
    private readonly CancellationTokenSource _pumpCancellation = new();
    private Task? _pump;
    private Guid _activationId;
    private Guid? _drainMessageId;
    private bool _disposed;

    public StdioCollectorProtocolBinding(
        TextReader input,
        TextWriter output,
        Guid collectorInstanceId,
        CollectorRuntimeArtifact artifact)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        if (collectorInstanceId == Guid.Empty)
            throw new ArgumentException("Collector Instance ID must not be empty.", nameof(collectorInstanceId));
        _collectorInstanceId = collectorInstanceId;
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
    }

    public static StdioCollectorProtocolBinding FromEnvironment(TextReader input, TextWriter output) => new(
        input,
        output,
        Guid.Parse(RequiredEnvironment("HEARTBEAT_COLLECTOR_INSTANCE_ID")),
        new CollectorRuntimeArtifact(
            RequiredEnvironment("HEARTBEAT_COLLECTOR_PACKAGE_ID"),
            RequiredEnvironment("HEARTBEAT_COLLECTOR_PACKAGE_VERSION"),
            RequiredEnvironment("HEARTBEAT_COLLECTOR_ARTIFACT_ID"),
            RequiredEnvironment("HEARTBEAT_COLLECTOR_ARTIFACT_HASH")));

    public async ValueTask<CollectorClientInitialization> StartAsync(
        CollectorClientDefinition definition,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var helloMessageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector.bootstrap/1",
            type = "activation.hello",
            messageId = helloMessageId,
            body = new
            {
                collectorInstanceId = _collectorInstanceId,
                runtimeArtifact = new
                {
                    packageId = _artifact.PackageId,
                    packageVersion = _artifact.PackageVersion,
                    artifactId = _artifact.ArtifactId,
                    artifactHash = _artifact.ArtifactHash
                },
                protocolMajors = new[] { 1 },
                supportedCapabilities = definition.Capabilities
            }
        }, cancellationToken);

        using (var accepted = await ReadDirectAsync(cancellationToken))
        {
            RequireResponse(accepted.RootElement, "activation.accepted", helloMessageId, bootstrap: true);
            var body = RequireObject(accepted.RootElement, "body");
            _activationId = ReadGuid(body, "activationId");
            if (ReadPositiveInt(body, "selectedProtocolMajor") != 1)
                throw new InvalidDataException("Hub selected an unsupported Collector Protocol major.");
            var selected = ReadSelectedCapabilities(body);

            using var initialize = await ReadDirectAsync(cancellationToken);
            RequireEnvelope(initialize.RootElement, "activation.initialize", _activationId);
            var initializeMessageId = ReadGuid(initialize.RootElement, "messageId");
            var initializeBody = RequireObject(initialize.RootElement, "body");
            var instance = RequireObject(initializeBody, "instance");
            var subject = RequireObject(instance, "subject");
            var spec = RequireObject(initializeBody, "spec");
            var config = RequireObject(spec, "config");
            var limits = RequireObject(initializeBody, "limits");
            var resources = RequireObject(initializeBody, "resources");
            var initialization = new CollectorClientInitialization(
                _activationId,
                ReadGuid(instance, "collectorInstanceId"),
                ReadGuid(subject, "subjectId"),
                ReadString(subject, "kind"),
                ReadPositiveLong(spec, "revision"),
                ReadPositiveInt(config, "version"),
                config.GetProperty("value").Clone(),
                ReadPositiveInt(limits, "maxFactsPerBatch"),
                ReadPositiveInt(limits, "maxBatchBytes"),
                ReadString(resources, "dataDirectory"),
                selected);
            _pump = Task.Run(PumpAsync, CancellationToken.None);
            _initializationReplyTo = initializeMessageId;
            return initialization;
        }
    }

    private Guid _initializationReplyTo;

    public async ValueTask<IReadOnlyDictionary<string, CollectorClientStream>> OpenStreamsAsync(
        long specRevision,
        IReadOnlyList<CollectorOutputBinding> outputs,
        CancellationToken cancellationToken)
    {
        await WriteNotificationAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.initialized",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            replyTo = _initializationReplyTo,
            body = new { appliedSpecRevision = specRevision }
        }, cancellationToken);
        var response = await RequestAsync(
            "streams.opened",
            messageId => new
            {
                protocol = "heartbeat.collector/1",
                type = "streams.open",
                messageId,
                activationId = _activationId,
                body = new
                {
                    specRevision,
                    bindings = outputs.Select(output => new
                    {
                        bindingId = output.BindingId,
                        outputId = output.OutputId,
                        dimensions = output.Dimensions
                    })
                }
            },
            cancellationToken);
        using (response)
        {
            var streams = RequireObject(response.RootElement, "body")
                .GetProperty("streams")
                .EnumerateArray()
                .Select(ReadStream)
                .ToDictionary(stream => stream.BindingId, StringComparer.Ordinal);
            if (streams.Count != outputs.Count || outputs.Any(output => !streams.ContainsKey(output.BindingId)))
                throw new InvalidDataException("Hub did not open every requested Collector Fact Stream.");
            return streams;
        }
    }

    public async ValueTask ReadyAsync(long appliedSpecRevision, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(
            "activation.readyAck",
            messageId => new
            {
                protocol = "heartbeat.collector/1",
                type = "activation.ready",
                messageId,
                activationId = _activationId,
                body = new { appliedSpecRevision }
            },
            cancellationToken);
        if (RequireObject(response.RootElement, "body").GetProperty("appliedSpecRevision").GetInt64() != appliedSpecRevision)
            throw new InvalidDataException("activation.readyAck appliedSpecRevision mismatch.");
    }

    public async ValueTask<CollectorFactBatchAcknowledgement> PublishAsync(
        Guid messageId,
        IReadOnlyList<BoundCollectorFact> facts,
        CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(
            ["facts.ack", "facts.rejected"],
            messageId,
            new
            {
                protocol = "heartbeat.collector/1",
                type = "facts.publish",
                messageId,
                activationId = _activationId,
                body = new { facts = facts.Select(WireFact) }
            },
            cancellationToken);
        var type = ReadString(response.RootElement, "type");
        var body = RequireObject(response.RootElement, "body");
        if (type == "facts.rejected")
            return new CollectorFactBatchAcknowledgement([], ReadError(body));
        var results = body.GetProperty("results").EnumerateArray().Select(ReadFactOutcome).ToArray();
        return new CollectorFactBatchAcknowledgement(results);
    }

    public async ValueTask<CollectorGapDeliveryOutcome> ReportGapAsync(
        Guid messageId,
        Guid streamId,
        CollectorStreamGap gap,
        CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(
            ["stream.gapAck", "stream.gapRejected"],
            messageId,
            new
            {
                protocol = "heartbeat.collector/1",
                type = "stream.gap",
                messageId,
                activationId = _activationId,
                body = new
                {
                    streamId,
                    gapId = gap.GapId,
                    factTime = new { start = Timestamp(gap.Start), end = Timestamp(gap.End) },
                    reason = gap.Reason,
                    estimatedFactsLost = gap.EstimatedFactsLost
                }
            },
            cancellationToken);
        if (ReadString(response.RootElement, "type") == "stream.gapAck")
            return new CollectorGapDeliveryOutcome(CollectorGapDeliveryStatus.Committed);
        var error = ReadError(RequireObject(response.RootElement, "body"));
        return new CollectorGapDeliveryOutcome(
            error.Retryable ? CollectorGapDeliveryStatus.Retry : CollectorGapDeliveryStatus.Rejected,
            error,
            error.Retryable ? 1_000 : null);
    }

    public async ValueTask<CollectorAuthorizationResponse> ChallengeAsync(
        Guid interactionId,
        string kind,
        string title,
        string? message,
        IReadOnlyList<CollectorAuthorizationField> fields,
        CancellationToken cancellationToken)
    {
        await WriteNotificationAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "auth.challenge",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            body = new { interactionId, kind, title, message, fields }
        }, cancellationToken);
        while (true)
        {
            using var response = await _authorizationResponses.Reader.ReadAsync(cancellationToken);
            var body = RequireObject(response.RootElement, "body");
            if (ReadGuid(body, "interactionId") != interactionId)
                throw new InvalidDataException("Hub answered a stale authorization interaction.");
            var values = RequireObject(body, "values").EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.GetString()
                            ?? throw new InvalidDataException("Authorization values must be strings."),
                StringComparer.Ordinal);
            return new CollectorAuthorizationResponse(interactionId, values);
        }
    }

    public ValueTask CompleteAuthorizationAsync(Guid interactionId, CancellationToken cancellationToken) =>
        WriteNotificationAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "auth.completed",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            body = new { interactionId }
        }, cancellationToken);

    public async ValueTask<string?> ReadSecretAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(
            "secret.value",
            messageId => new
            {
                protocol = "heartbeat.collector/1",
                type = "secret.read",
                messageId,
                activationId = _activationId,
                body = new { key }
            },
            cancellationToken);
        var body = RequireObject(response.RootElement, "body");
        return body.GetProperty("found").GetBoolean() ? body.GetProperty("value").GetString() : null;
    }

    public async ValueTask WriteSecretAsync(string key, string value, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(
            "secret.stored",
            messageId => new
            {
                protocol = "heartbeat.collector/1",
                type = "secret.write",
                messageId,
                activationId = _activationId,
                body = new { key, value }
            },
            cancellationToken);
    }

    public async ValueTask DeleteSecretAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(
            "secret.deleted",
            messageId => new
            {
                protocol = "heartbeat.collector/1",
                type = "secret.delete",
                messageId,
                activationId = _activationId,
                body = new { key }
            },
            cancellationToken);
    }

    public async ValueTask<CollectorDrainRequest> WaitForDrainAsync(CancellationToken cancellationToken)
    {
        var drain = await _drains.Reader.ReadAsync(cancellationToken);
        _drainMessageId = drain.RequestMessageId;
        return drain;
    }

    public ValueTask CompleteDrainAsync(CollectorDrainResult result, CancellationToken cancellationToken)
    {
        if (_drainMessageId is null)
            throw new InvalidOperationException("No Collector drain request is pending.");
        return WriteNotificationAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.drained",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            replyTo = _drainMessageId,
            body = new
            {
                appliedSpecRevision = result.AppliedSpecRevision,
                pendingFacts = result.PendingFacts,
                pendingGaps = result.PendingGaps,
                reason = CollectorProtocolDrainVocabulary.Format(result.Reason),
                remainderDurable = result.RemainderDurable
            }
        }, cancellationToken);
    }

    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_pumpCancellation.IsCancellationRequested)
            {
                var message = await ReadDirectAsync(_pumpCancellation.Token);
                var root = message.RootElement;
                RequireEnvelope(root, ReadString(root, "type"), _activationId);
                var type = ReadString(root, "type");
                if (type == "activation.drain")
                {
                    var body = RequireObject(root, "body");
                    var drain = new CollectorDrainRequest(
                        ReadGuid(root, "messageId"),
                        ReadTimestamp(body, "deadline"));
                    message.Dispose();
                    await _drains.Writer.WriteAsync(drain, _pumpCancellation.Token);
                    continue;
                }
                if (type == "auth.response")
                {
                    await _authorizationResponses.Writer.WriteAsync(message, _pumpCancellation.Token);
                    continue;
                }
                if (!root.TryGetProperty("replyTo", out var replyToElement) ||
                    !replyToElement.TryGetGuid(out var replyTo) ||
                    !_responses.TryRemove(replyTo, out var response))
                    throw new InvalidDataException($"Unexpected Hub Collector Protocol message '{type}'.");
                response.TrySetResult(message);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception;
        }
        finally
        {
            failure ??= new EndOfStreamException("Hub closed the Collector Protocol stream.");
            foreach (var response in _responses.Values)
                response.TrySetException(failure);
            _responses.Clear();
            _authorizationResponses.Writer.TryComplete(failure);
            _drains.Writer.TryComplete(failure);
        }
    }

    private Task<JsonDocument> RequestAsync(
        string expectedType,
        Func<Guid, object> request,
        CancellationToken cancellationToken) =>
        RequestAsync([expectedType], Guid.CreateVersion7(), request, cancellationToken);

    private Task<JsonDocument> RequestAsync(
        IReadOnlyList<string> expectedTypes,
        Guid messageId,
        object request,
        CancellationToken cancellationToken) =>
        RequestCoreAsync(expectedTypes, messageId, request, cancellationToken);

    private Task<JsonDocument> RequestAsync(
        IReadOnlyList<string> expectedTypes,
        Guid messageId,
        Func<Guid, object> request,
        CancellationToken cancellationToken) =>
        RequestCoreAsync(expectedTypes, messageId, request(messageId), cancellationToken);

    private async Task<JsonDocument> RequestCoreAsync(
        IReadOnlyList<string> expectedTypes,
        Guid messageId,
        object request,
        CancellationToken cancellationToken)
    {
        var response = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responses.TryAdd(messageId, response))
            throw new InvalidOperationException($"Collector Protocol messageId '{messageId}' is already in flight.");
        try
        {
            await WriteAsync(request, cancellationToken);
            var message = await response.Task.WaitAsync(cancellationToken);
            var type = ReadString(message.RootElement, "type");
            if (!expectedTypes.Contains(type, StringComparer.Ordinal))
            {
                message.Dispose();
                throw new InvalidDataException($"Expected {string.Join(" or ", expectedTypes)}, received '{type}'.");
            }
            return message;
        }
        catch
        {
            _responses.TryRemove(messageId, out _);
            throw;
        }
    }

    private ValueTask WriteNotificationAsync(object message, CancellationToken cancellationToken) =>
        new(WriteAsync(message, cancellationToken));

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<JsonDocument> ReadDirectAsync(CancellationToken cancellationToken)
    {
        var line = await _input.ReadLineAsync(cancellationToken)
            ?? throw new EndOfStreamException("Hub closed the Collector Protocol stream.");
        if (line.Length > 1_048_576)
            throw new InvalidDataException("Collector Protocol message exceeds 1 MiB.");
        return JsonDocument.Parse(line, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
    }

    private static object WireFact(BoundCollectorFact fact) => new
    {
        streamId = fact.StreamId,
        schemaRevision = fact.SchemaRevision,
        factId = fact.FactId,
        revision = fact.Revision,
        observedAt = fact.ObservedAt is null ? null : Timestamp(fact.ObservedAt.Value),
        recordState = fact.RecordState == CollectorFactRecordState.Present ? "present" : "retracted",
        time = fact.Time switch
        {
            CollectorSegmentFactTime segment => (object)new
            {
                start = Timestamp(segment.Start),
                end = Timestamp(segment.End),
                isFinal = segment.IsFinal
            },
            CollectorEventFactTime occurrence => new { occurredAt = Timestamp(occurrence.OccurredAt) },
            _ => throw new InvalidOperationException("Unknown Collector Fact time shape.")
        },
        payload = fact.RecordState == CollectorFactRecordState.Present ? fact.Payload : (JsonElement?)null
    };

    private static CollectorFactDeliveryOutcome ReadFactOutcome(JsonElement result)
    {
        var status = ReadString(result, "status") switch
        {
            "committed" => CollectorFactDeliveryStatus.Committed,
            "duplicate" => CollectorFactDeliveryStatus.Duplicate,
            "superseded" => CollectorFactDeliveryStatus.Superseded,
            "rejected" => CollectorFactDeliveryStatus.Rejected,
            "retry" => CollectorFactDeliveryStatus.Retry,
            var value => throw new InvalidDataException($"Unknown Fact delivery status '{value}'.")
        };
        return new CollectorFactDeliveryOutcome(
            result.GetProperty("index").GetInt32(),
            status,
            result.TryGetProperty("error", out _) ? ReadError(result) : null,
            result.TryGetProperty("retryAfterMs", out var retry) ? retry.GetInt32() : null);
    }

    private static CollectorProtocolError ReadError(JsonElement parent)
    {
        var error = RequireObject(parent, "error");
        return new CollectorProtocolError(
            ReadString(error, "code"),
            ReadString(error, "message"),
            error.GetProperty("retryable").GetBoolean());
    }

    private static CollectorClientStream ReadStream(JsonElement item)
    {
        var bindingId = ReadString(item, "bindingId");
        var stream = RequireObject(item, "stream");
        var subject = RequireObject(stream, "subject");
        var schema = RequireObject(stream, "schema");
        var dimensions = RequireObject(stream, "dimensions").EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString()
                        ?? throw new InvalidDataException("Stream dimensions must be strings."),
            StringComparer.Ordinal);
        return new CollectorClientStream(
            bindingId,
            ReadGuid(stream, "streamId"),
            ReadGuid(stream, "collectorInstanceId"),
            ReadGuid(subject, "subjectId"),
            ReadString(subject, "kind"),
            ReadString(stream, "outputId"),
            ReadString(stream, "source"),
            ReadString(stream, "factKind"),
            ReadString(schema, "id"),
            ReadPositiveInt(schema, "major"),
            ReadPositiveInt(schema, "revision"),
            ReadString(schema, "hash"),
            dimensions);
    }

    private static IReadOnlyDictionary<string, int> ReadSelectedCapabilities(JsonElement body) =>
        RequireObject(body, "selectedCapabilities").EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetInt32(),
            StringComparer.Ordinal);

    private static void RequireResponse(JsonElement root, string type, Guid replyTo, bool bootstrap = false)
    {
        if (ReadString(root, "protocol") != (bootstrap ? "heartbeat.collector.bootstrap/1" : "heartbeat.collector/1") ||
            ReadString(root, "type") != type || ReadGuid(root, "replyTo") != replyTo)
            throw new InvalidDataException($"Expected {type} response.");
    }

    private static void RequireEnvelope(JsonElement root, string type, Guid activationId)
    {
        if (ReadString(root, "protocol") != "heartbeat.collector/1" ||
            ReadString(root, "type") != type || ReadGuid(root, "activationId") != activationId)
            throw new InvalidDataException($"Invalid {type} Collector Protocol envelope.");
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{name} must be an object.");
        return value;
    }

    private static string ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"{name} must be a non-empty string.");
        return value.GetString()!;
    }

    private static Guid ReadGuid(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetGuid(out var result) && result != Guid.Empty
            ? result
            : throw new InvalidDataException($"{name} must be a non-empty UUID.");

    private static int ReadPositiveInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) && result > 0
            ? result
            : throw new InvalidDataException($"{name} must be a positive integer.");

    private static long ReadPositiveLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) && result > 0
            ? result
            : throw new InvalidDataException($"{name} must be a positive integer.");

    private static DateTimeOffset ReadTimestamp(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result) && result.Offset == TimeSpan.Zero
            ? result
            : throw new InvalidDataException($"{name} must be a UTC timestamp.");

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing {name}.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pumpCancellation.Cancel();
        if (_pump is not null)
        {
            var completed = await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromMilliseconds(100)));
            if (ReferenceEquals(completed, _pump))
            {
                try
                {
                    await _pump;
                }
                catch (OperationCanceledException)
                {
                    // Expected while closing the adapter.
                }
            }
        }
        if (_pump?.IsCompleted == true)
            _pumpCancellation.Dispose();
        _writeGate.Dispose();
    }
}
