using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Core;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

public sealed record BrowserExternalHostBindingOptions(
    string PackageDirectory,
    TimeSpan LeaseDuration,
    int FlushPeriodMilliseconds = 30_000)
{
    public string DataDirectory { get; init; } = string.Empty;

    public BrowserExternalHostBindingOptions(string packageDirectory)
        : this(packageDirectory, TimeSpan.FromSeconds(45)) { }
}

/// <summary>
/// Loopback HTTP binding for the official browser Collector. The lease is protocol-session
/// ownership only: expiry releases Runtime state and never attempts to terminate the browser.
/// </summary>
public sealed class BrowserExternalHostProtocolHandler : IExternalHostProtocolHttpHandler, IDisposable, IAsyncDisposable
{
    public const string RoutePrefix = "/v1/collector-protocol/browser";
    private readonly object _gate = new();
    private readonly CollectorRuntime _runtime;
    private readonly ICollectorDeclarationStore _declarations;
    private readonly BrowserCollectorRuntime _browserRuntime;
    private readonly TimeProvider _timeProvider;
    private readonly BrowserExternalHostBindingOptions _options;
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<Guid, HelloAttempt> _helloAttempts = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public BrowserExternalHostProtocolHandler(
        CollectorRuntime runtime,
        ICollectorDeclarationStore declarations,
        BrowserCollectorRuntime browserRuntime,
        BrowserExternalHostBindingOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(browserRuntime);
        ArgumentNullException.ThrowIfNull(options);
        if (options.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "LeaseDuration must be positive.");
        if (options.FlushPeriodMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "FlushPeriodMilliseconds must be positive.");
        _runtime = runtime;
        _declarations = declarations;
        _browserRuntime = browserRuntime;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _browserRuntime.AppDesiredEnabledChanged += HandleAppDesiredEnabledChanged;
    }

    public async ValueTask<ProtocolHttpResponse?> HandleAsync(
        string httpMethod,
        string? path,
        Stream body,
        CancellationToken cancellationToken = default)
    {
        if (path is null || !path.StartsWith(RoutePrefix, StringComparison.Ordinal))
            return null;
        ExpireLeases();
        Guid? replyTo = null;
        Guid? responseActivationId = null;
        string? rejectedType = null;
        var responseProtocol = "heartbeat.collector/1";

        async ValueTask<ProtocolMessage<T>> ReadRequest<T>(
            string protocol,
            string type,
            string failureType,
            Guid? activationId)
        {
            var message = await DeserializeAsync<ProtocolMessage<T>>(body, cancellationToken);
            replyTo = message.MessageId;
            responseActivationId = activationId;
            rejectedType = failureType;
            responseProtocol = protocol;
            if (message.Protocol != protocol || message.Type != type ||
                !IsUuidV7(message.MessageId) || message.Body is null ||
                message.ReplyTo is not null || message.ActivationId != activationId)
                throw new JsonException("Collector Protocol envelope is malformed or does not match the HTTP route.");
            return message;
        }

        try
        {
            if (httpMethod == "GET" && path == RoutePrefix)
                return Json(200, new { binding = "browser", protocolMajors = new[] { 1 } });
            if (httpMethod == "POST" && path == $"{RoutePrefix}/hello")
                return HandleHello(await ReadRequest<HelloRequest>(
                    "heartbeat.collector.bootstrap/1",
                    "activation.hello",
                    "activation.rejected",
                    null));
            if (!TryParseSessionPath(path, out var activationId, out var operation) || httpMethod != "POST")
                return Json(404, new { error = Error("protocol_invalid_message", "Unknown ExternalHost protocol route.") });
            return operation switch
            {
                "initialize" => HandleInitialize(activationId),
                "initialized" => HandleInitialized(
                    activationId,
                    await DeserializeAsync<ProtocolMessage<InitializedRequest>>(body, cancellationToken)),
                "streams" => HandleStreams(activationId, await ReadRequest<StreamsOpenRequest>(
                    "heartbeat.collector/1", "streams.open", "streams.rejected", activationId)),
                "ready" => HandleReady(activationId, await ReadRequest<ReadyRequest>(
                    "heartbeat.collector/1", "activation.ready", "activation.readyRejected", activationId)),
                "renew" => HandleRenew(activationId, await DeserializeAsync<RenewRequest>(body, cancellationToken)),
                "facts" => await HandleFactsAsync(
                    activationId,
                    await ReadRequest<PublishRequest>(
                        "heartbeat.collector/1", "facts.publish", "facts.rejected", activationId),
                    cancellationToken),
                "gap" => await HandleGapAsync(
                    activationId,
                    await ReadRequest<GapRequest>(
                        "heartbeat.collector/1", "stream.gap", "stream.gapRejected", activationId),
                    cancellationToken),
                "drained" => HandleDrained(activationId, await ReadRequest<DrainedRequest>(
                    "heartbeat.collector/1", "activation.drained", "activation.drainRejected", activationId)),
                _ => Json(404, new { error = Error("protocol_invalid_message", "Unknown ExternalHost protocol operation.") })
            };
        }
        catch (JsonException exception)
        {
            var error = Error("protocol_invalid_message", exception.Message);
            return replyTo is { } requestId && rejectedType is not null
                ? ProtocolResponse(
                    400,
                    rejectedType,
                    responseActivationId,
                    requestId,
                    new { error },
                    protocol: responseProtocol)
                : Json(400, new { error });
        }
        catch (CollectorActivationException exception)
        {
            return replyTo is { } requestId && rejectedType is not null
                ? ProtocolResponse(
                    exception.Error.Retryable ? 503 : 409,
                    rejectedType,
                    responseActivationId,
                    requestId,
                    new { error = exception.Error },
                    protocol: responseProtocol)
                : Json(exception.Error.Retryable ? 503 : 409, new { error = exception.Error });
        }
    }

    public void ExpireLeases()
    {
        var now = _timeProvider.GetUtcNow();
        Session[] expired;
        lock (_gate)
        {
            expired = _sessions.Values.Where(session => session.ExpiresAt <= now).ToArray();
            foreach (var session in expired)
            {
                _sessions.Remove(session.ActivationId);
            }
        }
        foreach (var session in expired)
        {
            if (session.Activation is null)
                _runtime.AbandonExternalHostActivation(session.ActivationId);
            else
                _runtime.StopExternalHostActivation(
                    session.Activation,
                    ExternalHostActivationStopReason.LeaseExpired);
        }
        foreach (var appHint in expired.Select(session => session.AppHint).Distinct(StringComparer.Ordinal))
            _browserRuntime.MarkWaiting(appHint, "浏览器未运行或连接租约已过期；启用意图保持不变。");
    }

    public async ValueTask DisposeAsync()
    {
        _browserRuntime.AppDesiredEnabledChanged -= HandleAppDesiredEnabledChanged;
        Session[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _helloAttempts.Clear();
        }
        foreach (var session in sessions)
        {
            if (session.Activation is null)
                _runtime.AbandonExternalHostActivation(session.ActivationId);
            else
                _runtime.StopExternalHostActivation(
                    session.Activation,
                    ExternalHostActivationStopReason.RuntimeStopping);
        }
        foreach (var appHint in sessions.Select(session => session.AppHint).Distinct(StringComparer.Ordinal))
            _browserRuntime.MarkWaiting(appHint, "Desktop Hub 正在停止；Package 和启用意图保持不变。");
        await ValueTask.CompletedTask;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private ProtocolHttpResponse HandleHello(ProtocolMessage<HelloRequest> message)
    {
        var request = message.Body;
        LocalCollectorPackage package;
        try
        {
            package = _browserRuntime.ResolvePackage(request.ArtifactId, request.ArtifactHash);
        }
        catch (PackageValidationException)
        {
            return HelloRejected(
                message.MessageId,
                Error("package_mismatch", "ExternalHost Artifact does not match an installed browser Package."),
                400);
        }
        var validationError = ValidateHello(request, package);
        if (validationError is not null)
        {
            if (validationError.Code == "package_mismatch" &&
                _browserRuntime.Current.RuntimeStatus != BrowserCollectorRuntimeStatus.Ready)
                _browserRuntime.MarkDegraded(
                    request.AppHint,
                    "浏览器仍在运行未知 Package；请在扩展页重新加载旁加载目录。");
            return HelloRejected(message.MessageId, validationError, 400);
        }
        var requestHash = HelloRequestHash(request);
        lock (_gate)
        {
            if (_helloAttempts.TryGetValue(message.MessageId, out var attempt))
            {
                if (attempt.RequestHash != requestHash)
                    return HelloRejected(
                        message.MessageId,
                        Error(
                            "protocol_invalid_message",
                            "The same activation.hello messageId was reused with different content."),
                        400);
                if (attempt.Error is not null)
                    return HelloRejected(message.MessageId, attempt.Error, 403);
                if (attempt.ActivationId is { } replayId && _sessions.TryGetValue(replayId, out var replay))
                    return HelloResponse(replay, message.MessageId);
                return HelloRejected(
                    message.MessageId,
                    Error("activation_stopping", "The original ExternalHost Activation attempt has ended."),
                    409);
            }
        }

        if (!_browserRuntime.IsAppDesiredEnabled(request.AppHint))
        {
            var error = Error("activation_stopping", "Browser Collector is disabled by Desired State.");
            lock (_gate)
                _helloAttempts[message.MessageId] = new HelloAttempt(null, error, requestHash);
            return HelloRejected(message.MessageId, error, 403);
        }
        var instance = _browserRuntime.GetOrCreateAppInstance(request.AppHint, package);
        Session[] replaced;
        lock (_gate)
            replaced = _sessions.Values.Where(session =>
                session.AppHint == BrowserCollectorRuntime.NormalizeAppHint(request.AppHint) &&
                session.ExternalHostIdentity == request.ExternalHostIdentity).ToArray();
        foreach (var old in replaced)
            StopAndRemove(old, ExternalHostActivationStopReason.LeaseReplaced);
        if (replaced.Length > 0)
            _browserRuntime.MarkWaiting(request.AppHint, "该 Host 的旧 Activation 已结束；等待新 Activation 就绪。");

        var activationId = Guid.CreateVersion7();
        var initialization = _runtime.BeginExternalHostActivation(
            instance.CollectorInstanceId,
            package,
            request.ArtifactId,
            request.ArtifactHash,
            new ProtocolSupport(request.ProtocolMajors, request.SupportedCapabilities),
            activationId,
            message.MessageId);
        var session = new Session(
            activationId,
            message.MessageId,
            Guid.CreateVersion7(),
            BrowserCollectorRuntime.NormalizeAppHint(request.AppHint),
            request.ExternalHostIdentity,
            package,
            initialization,
            false,
            null,
            null,
            _timeProvider.GetUtcNow() + _options.LeaseDuration);
        lock (_gate)
        {
            _sessions.Add(activationId, session);
            _helloAttempts[message.MessageId] = new HelloAttempt(activationId, null, requestHash);
        }
        return HelloResponse(session, message.MessageId);
    }

    private ProtocolHttpResponse HandleInitialize(Guid activationId)
    {
        var session = GetSession(activationId);
        return ProtocolResponse(200, "activation.initialize", activationId, null, new
        {
            instance = new
            {
                collectorInstanceId = session.Initialization.Instance.CollectorInstanceId,
                subject = session.Initialization.Instance.Subject
            },
            spec = new
            {
                revision = session.Initialization.Spec.SpecRevision,
                config = new
                {
                    version = session.Initialization.Spec.ConfigVersion,
                    value = session.Initialization.Spec.Config
                }
            },
            limits = session.Initialization.Limits,
            hubTime = _timeProvider.GetUtcNow()
        }, session.InitializeMessageId);
    }

    private ProtocolHttpResponse HandleInitialized(
        Guid activationId,
        ProtocolMessage<InitializedRequest> message)
    {
        var session = GetSession(activationId);
        if (message.Protocol != "heartbeat.collector/1" || message.Type != "activation.initialized" ||
            !IsUuidV7(message.MessageId) || message.ActivationId != activationId ||
            message.ReplyTo != session.InitializeMessageId || message.Body is null ||
            message.Body.AppliedSpecRevision != session.Initialization.Spec.SpecRevision)
            return ProtocolResponse(
                409,
                "activation.initializeRejected",
                activationId,
                message.MessageId,
                new { error = Error("spec_revision_stale", "Collector did not apply the current SpecRevision.") });
        if (!TryReplaceSession(session, session with { Initialized = true }))
            return ProtocolResponse(
                409,
                "activation.initializeRejected",
                activationId,
                message.MessageId,
                new { error = Error("activation_stopping", "ExternalHost Activation has ended.") });
        return new ProtocolHttpResponse(204, string.Empty, false);
    }

    private ProtocolHttpResponse HandleStreams(Guid activationId, ProtocolMessage<StreamsOpenRequest> message)
    {
        var request = message.Body;
        var session = GetSession(activationId);
        if (!session.Initialized)
            return ProtocolResponse(
                409,
                "streams.rejected",
                activationId,
                message.MessageId,
                new { error = Error("protocol_invalid_message", "activation.initialized is required before streams.open.") });
        if (session.Activation is not null)
            return StreamsResponse(session, message.MessageId);
        if (request.Bindings is null ||
            request.Bindings.Any(binding => binding is null || binding.Dimensions is null))
            return Rejected(
                400,
                "streams.rejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "streams.open bindings are malformed."));
        if (request.Bindings.Any(binding =>
                binding.Dimensions.ContainsKey("appHint") ||
                binding.Dimensions.ContainsKey("externalHostIdentity")))
            return Rejected(
                400,
                "streams.rejected",
                activationId,
                message.MessageId,
                Error(
                    "output_not_declared",
                    "ExternalHost identity dimensions are supplied by the binding and cannot be overridden."));
        var bindings = request.Bindings.Select(binding => new OutputBinding(
            binding.BindingId,
            binding.OutputId,
            binding.Dimensions
                .Append(new KeyValuePair<string, string>("appHint", session.AppHint))
                .Append(new KeyValuePair<string, string>("externalHostIdentity", session.ExternalHostIdentity))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))).ToArray();
        var activation = _runtime.OpenExternalHostStreams(
            activationId,
            request.SpecRevision,
            bindings);
        var opened = session with
        {
            Activation = activation,
            ExpiresAt = _timeProvider.GetUtcNow() + _options.LeaseDuration
        };
        if (!TryReplaceSession(session, opened))
        {
            _runtime.StopExternalHostActivation(activation, ExternalHostActivationStopReason.LeaseExpired);
            return Rejected(
                409,
                "streams.rejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost Activation has ended."));
        }
        return StreamsResponse(opened, message.MessageId);
    }

    private ProtocolHttpResponse HandleReady(Guid activationId, ProtocolMessage<ReadyRequest> message)
    {
        var request = message.Body;
        var session = GetSession(activationId);
        if (session.Activation is null)
            return Rejected(
                400,
                "activation.readyRejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "streams.opened is required before activation.ready."));
        if (session.Activation.State == CollectorActivationState.Ready)
            return ReadyResponse(session, message.MessageId);
        var activation = _runtime.MarkExternalHostReady(session.Activation, request.AppliedSpecRevision);
        var ready = session with
        {
            Activation = activation,
            LeaseToken = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
            ExpiresAt = _timeProvider.GetUtcNow() + _options.LeaseDuration
        };
        if (!TryReplaceSession(session, ready))
        {
            _runtime.StopExternalHostActivation(activation, ExternalHostActivationStopReason.LeaseExpired);
            return Rejected(
                409,
                "activation.readyRejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost Activation has ended."));
        }
        RegisterPackageDeclaration(ready.Package);
        _browserRuntime.MarkReady(ready.AppHint, ready.Package.PackageContentHash);
        return ReadyResponse(ready, message.MessageId);
    }

    private ProtocolHttpResponse HandleRenew(Guid activationId, RenewRequest request)
    {
        var session = GetSession(activationId);
        if (!_browserRuntime.IsAppDesiredEnabled(session.AppHint))
        {
            if (TryGetActiveLease(activationId, request.LeaseToken, out var disabledSession))
                StopAndRemove(disabledSession, ExternalHostActivationStopReason.DesiredDisabled);
            _browserRuntime.MarkWaiting(session.AppHint, "已停用；Package 和 App Instance 已保留。");
            return Json(409, new { error = Error("activation_stopping", "Browser Collector is disabled by Desired State.") });
        }
        if (!TryRenewLease(activationId, request.LeaseToken, out var renewed))
            return Json(409, new { error = Error("activation_stopping", "ExternalHost lease is not active.") });
        return Json(200, LeaseBody(renewed));
    }

    private async ValueTask<ProtocolHttpResponse> HandleFactsAsync(
        Guid activationId,
        ProtocolMessage<PublishRequest> message,
        CancellationToken cancellationToken)
    {
        var request = message.Body;
        if (!TryGetActiveLease(activationId, request.LeaseToken, out var session))
            return Rejected(
                409,
                "facts.rejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost lease is not active."));
        if (!_browserRuntime.IsAppDesiredEnabled(session.AppHint))
            return Rejected(
                403,
                "facts.rejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "Browser Collector is disabled by Desired State."));
        if (request.Facts is null || request.Facts.Count == 0)
            return Rejected(
                400,
                "facts.rejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "facts.publish must contain Facts."));
        var acknowledgement = await session.Activation!.PublishAsync(
            request.Facts[0].StreamId,
            message.MessageId,
            request.Facts,
            cancellationToken);
        if (acknowledgement.IsMessageRejected)
            return Rejected(400, "facts.rejected", activationId, message.MessageId, acknowledgement.MessageError!);
        return ProtocolResponse(
            200,
            "facts.ack",
            activationId,
            message.MessageId,
            new { results = acknowledgement.Results });
    }

    private async ValueTask<ProtocolHttpResponse> HandleGapAsync(
        Guid activationId,
        ProtocolMessage<GapRequest> message,
        CancellationToken cancellationToken)
    {
        var request = message.Body;
        if (!TryGetActiveLease(activationId, request.LeaseToken, out var session))
            return Rejected(
                409,
                "stream.gapRejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost lease is not active."));
        var outcome = await session.Activation!.ReportGapAsync(
            request.StreamId,
            message.MessageId,
            request.Gap,
            cancellationToken);
        return outcome.Status switch
        {
            GapDeliveryStatus.Rejected =>
                Rejected(400, "stream.gapRejected", activationId, message.MessageId, outcome.Error!),
            GapDeliveryStatus.Retry =>
                Rejected(503, "stream.gapRejected", activationId, message.MessageId, outcome.Error!),
            _ => ProtocolResponse(
                200,
                "stream.gapAck",
                activationId,
                message.MessageId,
                new { streamId = outcome.StreamId })
        };
    }

    private ProtocolHttpResponse HandleDrained(Guid activationId, ProtocolMessage<DrainedRequest> message)
    {
        var request = message.Body;
        if (!TryGetActiveLease(activationId, request.LeaseToken, out var session))
            return Rejected(
                409,
                "activation.drainRejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost lease is not active."));
        if (request.PendingFacts < 0 || request.PendingGaps < 0)
            return Rejected(
                400,
                "activation.drainRejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "Pending counts must not be negative."));
        if (!CollectorDrainVocabulary.TryParse(request.Reason, out var reason))
            return Rejected(
                400,
                "activation.drainRejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "Drain reason is not supported."));
        session.Activation!.CompleteDrain(new InProcessCollectorDrainResult(
            new InProcessCollectorLogicalDrainResult(
                request.PendingFacts,
                request.PendingGaps,
                reason,
                request.RemainderDurable)));
        StopAndRemove(session, ExternalHostActivationStopReason.CollectorDrained);
        return new ProtocolHttpResponse(204, string.Empty, false);
    }

    private void RegisterPackageDeclaration(LocalCollectorPackage package)
    {
        if (package.ObservationDeclaration is not { } declaration)
            return;
        _declarations.StoreVerifiedPackageDeclaration(ActivitySources.Browser, declaration.Json, declaration.Version);
    }

    private static CollectorProtocolError? ValidateHello(HelloRequest request, LocalCollectorPackage package)
    {
        if (string.IsNullOrWhiteSpace(request.AppHint) ||
            string.IsNullOrWhiteSpace(request.ExternalHostIdentity) ||
            request.ProtocolMajors is null || request.SupportedCapabilities is null)
            return Error(
                "protocol_invalid_message",
                "protocol support, appHint and externalHostIdentity are required.");
        try
        {
            _ = BrowserCollectorRuntime.NormalizeAppHint(request.AppHint);
        }
        catch (ArgumentException exception)
        {
            return Error("protocol_invalid_message", exception.Message);
        }
        if (request.ExternalHostIdentity.Length > 200 ||
            request.ExternalHostIdentity != request.ExternalHostIdentity.Trim())
            return Error(
                "protocol_invalid_message",
                "externalHostIdentity must be stable, trimmed and at most 200 characters.");
        var artifact = package.Artifacts.SingleOrDefault(candidate => candidate.ArtifactId == request.ArtifactId);
        if (artifact is null || !string.Equals(artifact.ContentHash, request.ArtifactHash, StringComparison.Ordinal))
            return Error("package_mismatch", "ExternalHost Artifact does not match the verified browser Package.");
        if (!request.ProtocolMajors.Contains(1))
            return Error("protocol_no_common_major", "No common Collector Protocol major.");
        foreach (var capability in new[] { "facts.segment", "diagnostics.stream-gap" })
        {
            if (!request.SupportedCapabilities.TryGetValue(capability, out var versions) ||
                versions is null || !versions.Contains(1))
                return Error(
                    "capability_no_common_version",
                    $"Required protocol capability '{capability}' has no common version.");
        }
        return null;
    }

    private static string HelloRequestHash(HelloRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("artifactId", request.ArtifactId);
            writer.WriteString("artifactHash", request.ArtifactHash);
            writer.WritePropertyName("protocolMajors");
            writer.WriteStartArray();
            foreach (var major in request.ProtocolMajors.Order())
                writer.WriteNumberValue(major);
            writer.WriteEndArray();
            writer.WritePropertyName("supportedCapabilities");
            writer.WriteStartObject();
            foreach (var capability in request.SupportedCapabilities.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                writer.WritePropertyName(capability.Key);
                writer.WriteStartArray();
                foreach (var version in capability.Value.Order())
                    writer.WriteNumberValue(version);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteString("appHint", request.AppHint);
            writer.WriteString("externalHostIdentity", request.ExternalHostIdentity);
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(buffer.ToArray()));
    }

    private Session GetSession(Guid activationId)
    {
        lock (_gate)
            return _sessions.TryGetValue(activationId, out var session)
                ? session
                : throw ActivationFailure("activation_stopping", "ExternalHost Activation was not found.");
    }

    private bool TryGetActiveLease(Guid activationId, string? leaseToken, out Session session)
    {
        session = GetSession(activationId);
        return session.Activation is not null && FixedTimeEquals(session.LeaseToken, leaseToken);
    }

    private bool TryReplaceSession(Session expected, Session replacement)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(expected.ActivationId, out var current) ||
                !ReferenceEquals(current, expected))
                return false;
            _sessions[replacement.ActivationId] = replacement;
            return true;
        }
    }

    private bool TryRenewLease(Guid activationId, string? leaseToken, out Session renewed)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(activationId, out var current) ||
                current.Activation is null || current.ExpiresAt <= _timeProvider.GetUtcNow() ||
                !FixedTimeEquals(current.LeaseToken, leaseToken))
            {
                renewed = null!;
                return false;
            }
            renewed = current with { ExpiresAt = _timeProvider.GetUtcNow() + _options.LeaseDuration };
            _sessions[activationId] = renewed;
            return true;
        }
    }

    private void StopAndRemove(Session session, ExternalHostActivationStopReason reason)
    {
        lock (_gate)
        {
            _sessions.Remove(session.ActivationId);
        }
        if (session.Activation is null)
            _runtime.AbandonExternalHostActivation(session.ActivationId);
        else
            _runtime.StopExternalHostActivation(session.Activation, reason);
    }

    private void HandleAppDesiredEnabledChanged(string appHint, bool enabled)
    {
        if (enabled)
            return;
        Session[] sessions;
        lock (_gate)
            sessions = _sessions.Values.Where(session => session.AppHint == appHint).ToArray();
        foreach (var session in sessions)
            StopAndRemove(session, ExternalHostActivationStopReason.DesiredDisabled);
        _browserRuntime.MarkWaiting(appHint, "已停用；Package 和 App Instance 已保留。");
    }

    private ProtocolHttpResponse HelloResponse(Session session, Guid replyTo) => ProtocolResponse(
        200,
        "activation.accepted",
        null,
        replyTo,
        new
        {
            activationId = session.ActivationId,
            selectedProtocolMajor = 1,
            selectedCapabilities = session.Initialization.SelectedCapabilities
        },
        protocol: "heartbeat.collector.bootstrap/1");

    private static ProtocolHttpResponse HelloRejected(
        Guid replyTo,
        CollectorProtocolError error,
        int statusCode) =>
        ProtocolResponse(
            statusCode,
            "activation.rejected",
            null,
            replyTo,
            new { error },
            protocol: "heartbeat.collector.bootstrap/1");

    private ProtocolHttpResponse ReadyResponse(Session session, Guid replyTo) => ProtocolResponse(
        200,
        "activation.readyAck",
        session.ActivationId,
        replyTo,
        new
        {
            appliedSpecRevision = session.Initialization.Spec.SpecRevision,
            lease = LeaseBody(session)
        });

    private ProtocolHttpResponse StreamsResponse(Session session, Guid replyTo) => ProtocolResponse(
        200,
        "streams.opened",
        session.ActivationId,
        replyTo,
        new { streams = session.Activation!.Streams });

    private object LeaseBody(Session session) => new
    {
        token = session.LeaseToken,
        durationMs = (int)_options.LeaseDuration.TotalMilliseconds,
        expiresAt = session.ExpiresAt
    };

    private static bool TryParseSessionPath(string path, out Guid activationId, out string operation)
    {
        activationId = Guid.Empty;
        operation = string.Empty;
        if (path.Length <= RoutePrefix.Length + 1 || path[RoutePrefix.Length] != '/')
            return false;
        var tail = path[(RoutePrefix.Length + 1)..].Split('/');
        return tail.Length == 2 && Guid.TryParse(tail[0], out activationId) &&
               (operation = tail[1]).Length > 0;
    }

    private static async ValueTask<T> DeserializeAsync<T>(Stream body, CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<T>(body, JsonOptions, cancellationToken)
        ?? throw new JsonException("Protocol request body is required.");

    private static ProtocolHttpResponse Json(int statusCode, object body) =>
        new(statusCode, JsonSerializer.Serialize(body, JsonOptions));

    private static ProtocolHttpResponse ProtocolResponse(
        int statusCode,
        string type,
        Guid? activationId,
        Guid? replyTo,
        object body,
        Guid? messageId = null,
        string protocol = "heartbeat.collector/1") =>
        Json(statusCode, new
        {
            protocol,
            type,
            messageId = messageId ?? Guid.CreateVersion7(),
            activationId,
            replyTo,
            body
        });

    private static ProtocolHttpResponse Rejected(
        int statusCode,
        string type,
        Guid activationId,
        Guid replyTo,
        CollectorProtocolError error) =>
        ProtocolResponse(statusCode, type, activationId, replyTo, new { error });

    private static CollectorProtocolError Error(string code, string message) => new(code, message, false);

    private static CollectorActivationException ActivationFailure(string code, string message) =>
        new(new CollectorProtocolError(code, message, false));

    private static bool FixedTimeEquals(string? left, string? right) =>
        left is not null && right is not null &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return value != Guid.Empty && text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private sealed record Session(
        Guid ActivationId,
        Guid HelloMessageId,
        Guid InitializeMessageId,
        string AppHint,
        string ExternalHostIdentity,
        LocalCollectorPackage Package,
        ExternalHostCollectorInitialization Initialization,
        bool Initialized,
        ExternalHostCollectorActivation? Activation,
        string? LeaseToken,
        DateTimeOffset ExpiresAt);

    private sealed record HelloAttempt(
        Guid? ActivationId,
        CollectorProtocolError? Error,
        string RequestHash);

    public sealed record ProtocolMessage<T>(
        string Protocol,
        string Type,
        Guid MessageId,
        Guid? ActivationId,
        Guid? ReplyTo,
        T Body);

    public sealed record HelloRequest(
        string ArtifactId,
        string ArtifactHash,
        IReadOnlyList<int> ProtocolMajors,
        IReadOnlyDictionary<string, IReadOnlyList<int>> SupportedCapabilities,
        string AppHint,
        string ExternalHostIdentity);

    public sealed record BindingRequest(
        string BindingId,
        string OutputId,
        IReadOnlyDictionary<string, string> Dimensions);

    public sealed record InitializedRequest(long AppliedSpecRevision);

    public sealed record StreamsOpenRequest(
        long SpecRevision,
        IReadOnlyList<BindingRequest> Bindings);

    public sealed record ReadyRequest(long AppliedSpecRevision);

    public sealed record RenewRequest(string LeaseToken);

    public sealed record PublishRequest(
        string LeaseToken,
        IReadOnlyList<FactSubmission> Facts);

    public sealed record GapRequest(
        string LeaseToken,
        Guid StreamId,
        StreamGapReport Gap);

    public sealed record DrainedRequest(
        string LeaseToken,
        long AppliedSpecRevision,
        int PendingFacts,
        int PendingGaps,
        string Reason,
        bool RemainderDurable);
}
