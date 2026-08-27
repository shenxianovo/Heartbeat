using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Heartbeat.Collector.VRChat;

internal sealed class VRChatManagedCollector(
    TextReader input,
    TextWriter output,
    IVRChatApiFactory apiFactory,
    Func<DateTimeOffset>? clock = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private Guid _activationId;
    private long _specRevision;
    private Guid _streamId;
    private TimeSpan _pollInterval = TimeSpan.FromMinutes(1);
    private IVRChatApiSession? _session;
    private PresenceStateMachine _presence = new();
    private VRChatOutbox? _outbox;
    private Channel<JsonDocument>? _inbox;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var helloMessageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector.bootstrap/1",
            type = "activation.hello",
            messageId = helloMessageId,
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
                supportedCapabilities = new Dictionary<string, int[]>
                {
                    ["facts.segment"] = [1],
                    ["auth.interactive"] = [1],
                    ["secrets.instance"] = [1],
                    ["resources.instance-data"] = [1],
                    ["diagnostics.stream-gap"] = [1]
                }
            }
        }, cancellationToken);

        using (var accepted = await ReadDirectAsync(cancellationToken))
        {
            Require(accepted.RootElement, "activation.accepted", helloMessageId);
            _activationId = accepted.RootElement.GetProperty("body").GetProperty("activationId").GetGuid();
            var selected = accepted.RootElement.GetProperty("body").GetProperty("selectedCapabilities");
            foreach (var required in new[] { "facts.segment", "auth.interactive", "secrets.instance", "resources.instance-data", "diagnostics.stream-gap" })
            {
                if (!selected.TryGetProperty(required, out _))
                    throw new InvalidOperationException($"Hub did not select required capability '{required}'.");
            }
        }

        Guid initializeMessageId;
        using (var initialize = await ReadDirectAsync(cancellationToken))
        {
            Require(initialize.RootElement, "activation.initialize", activationId: _activationId);
            initializeMessageId = initialize.RootElement.GetProperty("messageId").GetGuid();
            var body = initialize.RootElement.GetProperty("body");
            var subject = body.GetProperty("instance").GetProperty("subject");
            if (subject.GetProperty("kind").GetString() != "account")
                throw new InvalidOperationException("VRChat Collector requires an Account Subject.");
            _specRevision = body.GetProperty("spec").GetProperty("revision").GetInt64();
            var config = body.GetProperty("spec").GetProperty("config").GetProperty("value");
            if (config.TryGetProperty("pollIntervalSeconds", out var interval) &&
                interval.TryGetInt32(out var seconds) && seconds > 0)
                _pollInterval = TimeSpan.FromSeconds(seconds);
            var dataDirectory = body.GetProperty("resources").GetProperty("dataDirectory").GetString()
                ?? throw new InvalidOperationException("Hub did not provide an Instance data directory.");
            _outbox = VRChatOutbox.Open(Path.Combine(dataDirectory, "vrchat-outbox.json"));
        }

        await EnsureAuthorizedAsync(cancellationToken);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.initialized",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            replyTo = initializeMessageId,
            body = new { appliedSpecRevision = _specRevision }
        }, cancellationToken);

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
                bindings = new[] { new { bindingId = "presence", outputId = "presence", dimensions = new { } } }
            }
        }, cancellationToken);
        using (var opened = await ReadDirectAsync(cancellationToken))
        {
            Require(opened.RootElement, "streams.opened", openMessageId, _activationId);
            _streamId = opened.RootElement.GetProperty("body").GetProperty("streams")[0]
                .GetProperty("stream").GetProperty("streamId").GetGuid();
        }

        var readyMessageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.ready",
            messageId = readyMessageId,
            activationId = _activationId,
            body = new { appliedSpecRevision = _specRevision }
        }, cancellationToken);
        using (var ready = await ReadDirectAsync(cancellationToken))
            Require(ready.RootElement, "activation.readyAck", readyMessageId, _activationId);

        StartInbox(cancellationToken);
        _outbox.RecoverInterruptedPresence(_clock());
        await FlushOutboxAsync(cancellationToken);
        await SynchronizePresenceAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var messageReady = _inbox!.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var poll = Task.Delay(_pollInterval, cancellationToken);
            var completed = await Task.WhenAny(messageReady, poll);
            if (completed == poll)
            {
                await poll;
                await SynchronizePresenceAsync(cancellationToken);
                continue;
            }
            if (!await messageReady || !_inbox.Reader.TryRead(out var message))
                throw new EndOfStreamException("Hub closed the Collector Protocol stream.");
            using (message)
            {
                var type = message.RootElement.GetProperty("type").GetString();
                if (type != "activation.drain")
                    throw new InvalidOperationException($"Unexpected Hub message '{type}'.");
                await DrainAsync(message.RootElement, cancellationToken);
                return;
            }
        }
    }

    private async Task SynchronizePresenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var observed = await _session!.GetPresenceAsync(cancellationToken);
            if (observed is not null)
            {
                var worldName = await _session.GetWorldNameAsync(observed.WorldId, cancellationToken);
                observed = observed with { WorldName = worldName };
            }
            foreach (var fact in _presence.Observe(observed, _clock()))
                _outbox!.Enqueue(fact);
            await FlushOutboxAsync(cancellationToken);
        }
        catch (VRChatUnauthorizedException)
        {
            foreach (var fact in _presence.Stop(_clock()))
                _outbox!.Enqueue(fact);
            await FlushOutboxAsync(cancellationToken);
            await DeleteSecretAsync("session", cancellationToken);
            await EnsureAuthorizedAsync(cancellationToken);
        }
        catch (VRChatTransientException)
        {
            // Preserve the active segment. The next poll retries without inventing a transition.
        }
    }

    private async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        var saved = await ReadSecretAsync("session", cancellationToken);
        if (saved is not null)
        {
            try
            {
                var resumed = apiFactory.FromSession(saved);
                var state = await RetryTransientAsync(resumed.AuthenticateAsync, cancellationToken);
                if (state.RequiredTwoFactorMethods.Count == 0)
                {
                    _session = resumed;
                    return;
                }
            }
            catch (Exception exception) when (exception is VRChatUnauthorizedException or JsonException)
            {
                await DeleteSecretAsync("session", cancellationToken);
            }
        }

        string? error = null;
        while (true)
        {
            var credentials = await ChallengeAsync(
                "credentials",
                "登录 VRChat",
                error,
                [
                    new ChallengeField("username", "用户名或邮箱", false, "text"),
                    new ChallengeField("password", "密码", true, "text")
                ],
                cancellationToken);
            try
            {
                var candidate = apiFactory.FromCredentials(
                    credentials.Values["username"],
                    credentials.Values["password"]);
                var state = await RetryTransientAsync(candidate.AuthenticateAsync, cancellationToken);
                var lastInteractionId = credentials.InteractionId;
                if (state.RequiredTwoFactorMethods.Count > 0)
                {
                    var method = state.RequiredTwoFactorMethods.Contains("emailOtp", StringComparer.Ordinal)
                        ? "emailOtp"
                        : "totp";
                    var verification = await ChallengeAsync(
                        "verificationCode",
                        method == "emailOtp" ? "输入邮件验证码" : "输入两步验证码",
                        null,
                        [new ChallengeField("code", "验证码", true, "numeric")],
                        cancellationToken);
                    await RetryTransientAsync(
                        token => candidate.VerifyTwoFactorAsync(
                            method,
                            verification.Values["code"],
                            token),
                        cancellationToken);
                    state = await RetryTransientAsync(candidate.AuthenticateAsync, cancellationToken);
                    lastInteractionId = verification.InteractionId;
                }
                if (state.RequiredTwoFactorMethods.Count != 0)
                    throw new VRChatUnauthorizedException("VRChat authentication remains incomplete.");
                await WriteSecretAsync("session", candidate.ExportSession(), cancellationToken);
                _session = candidate;
                await WriteAsync(new
                {
                    protocol = "heartbeat.collector/1",
                    type = "auth.completed",
                    messageId = Guid.CreateVersion7(),
                    activationId = _activationId,
                    body = new { interactionId = lastInteractionId }
                }, cancellationToken);
                return;
            }
            catch (VRChatUnauthorizedException)
            {
                error = "登录失败，请检查账号信息后重试。";
            }
        }
    }

    private static async Task<T> RetryTransientAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try { return await operation(cancellationToken); }
            catch (VRChatTransientException)
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private static async Task RetryTransientAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await RetryTransientAsync(async token =>
        {
            await operation(token);
            return true;
        }, cancellationToken);
    }

    private async Task<ChallengeResponse> ChallengeAsync(
        string kind,
        string title,
        string? message,
        IReadOnlyList<ChallengeField> fields,
        CancellationToken cancellationToken)
    {
        var interactionId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "auth.challenge",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            body = new { interactionId, kind, title, message, fields }
        }, cancellationToken);
        using var response = await ReadAsync(cancellationToken);
        Require(response.RootElement, "auth.response", activationId: _activationId);
        var body = response.RootElement.GetProperty("body");
        if (body.GetProperty("interactionId").GetGuid() != interactionId)
            throw new InvalidOperationException("Hub answered a stale authorization interaction.");
        var values = body.GetProperty("values").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        return new ChallengeResponse(interactionId, values);
    }

    private async Task<string?> ReadSecretAsync(string key, CancellationToken cancellationToken)
    {
        var messageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "secret.read",
            messageId,
            activationId = _activationId,
            body = new { key }
        }, cancellationToken);
        using var response = await ReadAsync(cancellationToken);
        Require(response.RootElement, "secret.value", messageId, _activationId);
        var body = response.RootElement.GetProperty("body");
        return body.GetProperty("found").GetBoolean() ? body.GetProperty("value").GetString() : null;
    }

    private async Task WriteSecretAsync(string key, string value, CancellationToken cancellationToken)
    {
        var messageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "secret.write",
            messageId,
            activationId = _activationId,
            body = new { key, value }
        }, cancellationToken);
        using var response = await ReadAsync(cancellationToken);
        Require(response.RootElement, "secret.stored", messageId, _activationId);
    }

    private async Task DeleteSecretAsync(string key, CancellationToken cancellationToken)
    {
        var messageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "secret.delete",
            messageId,
            activationId = _activationId,
            body = new { key }
        }, cancellationToken);
        using var response = await ReadAsync(cancellationToken);
        Require(response.RootElement, "secret.deleted", messageId, _activationId);
    }

    private async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        while (_outbox!.PendingFacts.Count > 0)
        {
            var batch = _outbox.PendingFacts.Take(100).ToArray();
            var messageId = Guid.CreateVersion7();
            await WriteAsync(new
            {
                protocol = "heartbeat.collector/1",
                type = "facts.publish",
                messageId,
                activationId = _activationId,
                body = new
                {
                    facts = batch.Select(fact => new
                    {
                        streamId = _streamId,
                        schemaRevision = 1,
                        factId = fact.FactId,
                        revision = fact.Revision,
                        observedAt = Timestamp(fact.End),
                        recordState = "present",
                        time = new
                        {
                            start = Timestamp(fact.Start),
                            end = Timestamp(fact.End),
                            isFinal = fact.IsFinal
                        },
                        payload = new
                        {
                            identityKey = fact.IdentityKey,
                            title = fact.Title,
                            appDisplayName = "VRChat",
                            worldId = fact.WorldId,
                            worldName = fact.WorldName,
                            instanceId = fact.InstanceId
                        }
                    })
                }
            }, cancellationToken);
            using var response = await ReadAsync(cancellationToken);
            Require(response.RootElement, "facts.ack", messageId, _activationId);
            foreach (var result in response.RootElement.GetProperty("body").GetProperty("results").EnumerateArray())
            {
                var index = result.GetProperty("index").GetInt32();
                var status = result.GetProperty("status").GetString();
                if (status is "committed" or "duplicate" or "superseded")
                    _outbox.AcknowledgeFact(batch[index].FactId, batch[index].Revision);
            }
            if (_outbox.PendingFacts.Any(fact => batch.Any(item => item.FactId == fact.FactId)))
                break;
        }

        foreach (var gap in _outbox.PendingGaps.ToArray())
        {
            var messageId = gap.GapId;
            await WriteAsync(new
            {
                protocol = "heartbeat.collector/1",
                type = "stream.gap",
                messageId,
                activationId = _activationId,
                body = new
                {
                    streamId = _streamId,
                    factTime = new { start = Timestamp(gap.Start), end = Timestamp(gap.End) },
                    reason = gap.Reason,
                    estimatedFactsLost = gap.EstimatedFactsLost
                }
            }, cancellationToken);
            using var response = await ReadAsync(cancellationToken);
            Require(response.RootElement, "stream.gapAck", messageId, _activationId);
            _outbox.AcknowledgeGap(gap.GapId);
        }
    }

    private async Task DrainAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var messageId = request.GetProperty("messageId").GetGuid();
        foreach (var fact in _presence.Stop(_clock()))
            _outbox!.Enqueue(fact);
        await FlushOutboxAsync(cancellationToken);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.drained",
            messageId = Guid.CreateVersion7(),
            activationId = _activationId,
            replyTo = messageId,
            body = new
            {
                appliedSpecRevision = _specRevision,
                pendingFacts = _outbox!.PendingFacts.Count,
                pendingGaps = _outbox.PendingGaps.Count
            }
        }, cancellationToken);
    }

    private void StartInbox(CancellationToken cancellationToken)
    {
        _inbox = Channel.CreateUnbounded<JsonDocument>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await input.ReadLineAsync(cancellationToken) is { } line)
                    await _inbox.Writer.WriteAsync(JsonDocument.Parse(line), cancellationToken);
                _inbox.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                _inbox.Writer.TryComplete(exception);
            }
        }, CancellationToken.None);
    }

    private Task<JsonDocument> ReadAsync(CancellationToken cancellationToken) =>
        _inbox is null
            ? ReadDirectAsync(cancellationToken)
            : _inbox.Reader.ReadAsync(cancellationToken).AsTask();

    private async Task<JsonDocument> ReadDirectAsync(CancellationToken cancellationToken)
    {
        var line = await input.ReadLineAsync(cancellationToken)
            ?? throw new EndOfStreamException("Hub closed the Collector Protocol stream.");
        return JsonDocument.Parse(line);
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            JsonSerializer.Serialize(message, SerializerOptions).AsMemory(),
            cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static void Require(
        JsonElement message,
        string type,
        Guid? replyTo = null,
        Guid? activationId = null)
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

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");

    private sealed record ChallengeField(
        string Name,
        string Label,
        bool IsSecret,
        string InputMode);

    private sealed record ChallengeResponse(
        Guid InteractionId,
        IReadOnlyDictionary<string, string> Values);
}
