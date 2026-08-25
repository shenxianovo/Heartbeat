using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collector.Reference.ManagedProcess;

if (args is ["--create-package", var packageDirectory])
{
    ReferencePackageBuilder.Create(packageDirectory);
    return;
}

var collector = new ReferenceManagedProcessCollector(Console.In, Console.Out);
await collector.RunAsync();

internal sealed class ReferenceManagedProcessCollector(TextReader input, TextWriter output)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private Guid _activationId;
    private long _specRevision;
    private Guid _streamId;

    public async Task RunAsync()
    {
        var behavior = Environment.GetEnvironmentVariable("HEARTBEAT_REFERENCE_BEHAVIOR");
        if (behavior == "startup_timeout")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        if (behavior == "malformed")
        {
            await output.WriteLineAsync("{not-json");
            await output.FlushAsync();
            return;
        }
        if (behavior == "exit_before_hello")
            return;

        var helloMessageId = Guid.CreateVersion7();
        var capabilities = new Dictionary<string, int[]>
        {
            ["facts.segment"] = [1],
            ["diagnostics.stream-gap"] = [1]
        };
        if (behavior == "extra_capability")
            capabilities["reference.unsupported"] = [1];
        object advertisedCapabilities = behavior == "invalid_capability_type"
            ? new Dictionary<string, object> { ["facts.segment"] = 1 }
            : capabilities;
        object helloMessageIdValue = behavior == "uppercase_uuid"
            ? helloMessageId.ToString("D").ToUpperInvariant()
            : helloMessageId;
        var hello = JsonSerializer.SerializeToNode(new
        {
            protocol = "heartbeat.collector.bootstrap/1",
            type = "activation.hello",
            messageId = helloMessageIdValue,
            body = new
            {
                collectorInstanceId = RequiredGuid("HEARTBEAT_COLLECTOR_INSTANCE_ID"),
                runtimeArtifact = new
                {
                    packageId = Required("HEARTBEAT_COLLECTOR_PACKAGE_ID"),
                    packageVersion = Required("HEARTBEAT_COLLECTOR_PACKAGE_VERSION"),
                    artifactId = Required("HEARTBEAT_COLLECTOR_ARTIFACT_ID"),
                    artifactHash = Required("HEARTBEAT_COLLECTOR_ARTIFACT_HASH")
                },
                protocolMajors = new[] { 1 },
                supportedCapabilities = advertisedCapabilities
            }
        }, SerializerOptions)!.AsObject();
        if (behavior == "unknown_hello_field")
            hello["body"]!.AsObject()["unexpected"] = true;
        await WriteAsync(hello);

        using var accepted = await ReadAsync();
        Require(accepted.RootElement, "activation.accepted", helloMessageId);
        _activationId = accepted.RootElement.GetProperty("body").GetProperty("activationId").GetGuid();
        if (accepted.RootElement.GetProperty("body").GetProperty("selectedCapabilities")
            .TryGetProperty("reference.unsupported", out _))
            throw new InvalidOperationException("Hub selected a capability absent from its own and the Package's support.");

        using var initialize = await ReadAsync();
        Require(initialize.RootElement, "activation.initialize", activationId: _activationId);
        var initializeMessageId = initialize.RootElement.GetProperty("messageId").GetGuid();
        var initializeBody = initialize.RootElement.GetProperty("body");
        var subject = initializeBody.GetProperty("instance").GetProperty("subject");
        if (subject.GetProperty("kind").GetString() != "account")
            throw new InvalidOperationException("Reference ManagedProcess Collector requires an Account Subject.");
        _specRevision = initializeBody.GetProperty("spec").GetProperty("revision").GetInt64();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.initialized",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            replyTo = initializeMessageId,
            body = new { appliedSpecRevision = _specRevision }
        });

        var openMessageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "streams.open",
            messageId = openMessageId,
            activationId = _activationId,
            body = new
            {
                specRevision = _specRevision,
                bindings = new[] { new { bindingId = "activity", outputId = "activity", dimensions = new { } } }
            }
        });

        using var opened = await ReadAsync();
        Require(opened.RootElement, "streams.opened", openMessageId, _activationId);
        _streamId = opened.RootElement.GetProperty("body").GetProperty("streams")[0]
            .GetProperty("stream").GetProperty("streamId").GetGuid();

        var readyMessageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.ready",
            messageId = readyMessageId,
            activationId = _activationId,
            body = new { appliedSpecRevision = _specRevision }
        });
        using var ready = await ReadAsync();
        Require(ready.RootElement, "activation.readyAck", readyMessageId, _activationId);

        if (behavior == "exit_after_ready")
            return;
        if (behavior == "corrupt_after_ready")
        {
            await output.WriteLineAsync("[broken");
            await output.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        await PublishReferenceFactAsync();
        while (true)
        {
            using var message = await ReadAsync();
            var type = message.RootElement.GetProperty("type").GetString();
            if (type == "facts.ack")
                continue;
            if (type != "activation.drain")
                throw new InvalidOperationException($"Unexpected Hub message '{type}'.");
            if (behavior == "corrupt_on_drain")
            {
                await output.WriteLineAsync("{broken-drain");
                await output.FlushAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return;
            }
            if (behavior == "ignore_drain")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return;
            }
            var drainMessageId = message.RootElement.GetProperty("messageId").GetGuid();
            await WriteAsync(new
            {
                protocol = "heartbeat.collector/1",
                type = "activation.drained",
                messageId = Guid.CreateVersion7(),
                activationId = _activationId,
                replyTo = drainMessageId,
                body = new { appliedSpecRevision = _specRevision, pendingFacts = 0, pendingGaps = 0 }
            });
            return;
        }
    }

    private async Task PublishReferenceFactAsync()
    {
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "facts.publish",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            body = new
            {
                facts = new[]
                {
                    new
                    {
                        streamId = _streamId,
                        schemaRevision = 1,
                        factId = Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
                        revision = 1,
                        observedAt = "2026-08-22T12:05:00.0000000Z",
                        recordState = "present",
                        time = new
                        {
                            start = "2026-08-22T12:00:00.0000000Z",
                            end = "2026-08-22T12:05:00.0000000Z",
                            isFinal = false
                        },
                        payload = new { identityKey = "reference.account|online", title = "Reference account online" }
                    }
                }
            }
        });
    }

    private async Task<JsonDocument> ReadAsync()
    {
        var line = await input.ReadLineAsync() ?? throw new EndOfStreamException("Hub closed the protocol stream.");
        return JsonDocument.Parse(line);
    }

    private async Task WriteAsync(object message)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(message, SerializerOptions));
        await output.FlushAsync();
    }

    private static void Require(JsonElement message, string type, Guid? replyTo = null, Guid? activationId = null)
    {
        if (message.GetProperty("type").GetString() != type)
            throw new InvalidOperationException($"Expected {type}.");
        if (replyTo is not null && message.GetProperty("replyTo").GetGuid() != replyTo)
            throw new InvalidOperationException($"{type} replyTo mismatch.");
        if (activationId is not null && message.GetProperty("activationId").GetGuid() != activationId)
            throw new InvalidOperationException($"{type} activationId mismatch.");
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing {name}.");

    private static Guid RequiredGuid(string name) => Guid.Parse(Required(name));
}
