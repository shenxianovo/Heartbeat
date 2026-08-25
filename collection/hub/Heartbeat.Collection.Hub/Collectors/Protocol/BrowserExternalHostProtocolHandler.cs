using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

public sealed record BrowserExternalHostBindingOptions(
    string PackageDirectory,
    TimeSpan LeaseDuration,
    int FlushPeriodMilliseconds = 30_000)
{
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
    private const string Source = "browser";
    private readonly object _gate = new();
    private readonly CollectorRuntime _runtime;
    private readonly ICollectorRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly BrowserExternalHostBindingOptions _options;
    private readonly LocalCollectorPackage _package;
    private readonly SubjectReference _subject;
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<Guid, Guid> _helloAttempts = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public BrowserExternalHostProtocolHandler(
        CollectorRuntime runtime,
        ICollectorRegistry registry,
        IDeviceIdentity deviceIdentity,
        BrowserExternalHostBindingOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(deviceIdentity);
        ArgumentNullException.ThrowIfNull(options);
        if (options.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "LeaseDuration must be positive.");
        if (options.FlushPeriodMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "FlushPeriodMilliseconds must be positive.");
        if (!Guid.TryParse(deviceIdentity.HardwareId, out var subjectId) || subjectId == Guid.Empty)
            throw new InvalidOperationException("Browser Collector requires a UUID machine identity.");

        _runtime = runtime;
        _registry = registry;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _package = LocalCollectorPackage.Load(options.PackageDirectory);
        _subject = new SubjectReference(subjectId, SubjectKind.Machine);
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
        try
        {
            if (httpMethod == "POST" && path == $"{RoutePrefix}/hello")
                return HandleHello(await DeserializeAsync<HelloRequest>(body, cancellationToken));
            if (!TryParseSessionPath(path, out var activationId, out var operation) || httpMethod != "POST")
                return Json(404, new { error = Error("protocol_invalid_message", "Unknown ExternalHost protocol route.") });
            return operation switch
            {
                "ready" => HandleReady(activationId, await DeserializeAsync<ReadyRequest>(body, cancellationToken)),
                "renew" => HandleRenew(activationId, await DeserializeAsync<RenewRequest>(body, cancellationToken)),
                "facts" => await HandleFactsAsync(
                    activationId,
                    await DeserializeAsync<PublishRequest>(body, cancellationToken),
                    cancellationToken),
                "gap" => await HandleGapAsync(
                    activationId,
                    await DeserializeAsync<GapRequest>(body, cancellationToken),
                    cancellationToken),
                "drained" => HandleDrained(activationId, await DeserializeAsync<DrainedRequest>(body, cancellationToken)),
                _ => Json(404, new { error = Error("protocol_invalid_message", "Unknown ExternalHost protocol operation.") })
            };
        }
        catch (JsonException exception)
        {
            return Json(400, new { error = Error("protocol_invalid_message", exception.Message) });
        }
        catch (CollectorActivationException exception)
        {
            return Json(exception.Error.Retryable ? 503 : 409, new { error = exception.Error });
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
                _helloAttempts.Remove(session.HelloMessageId);
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
    }

    public async ValueTask DisposeAsync()
    {
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
        await ValueTask.CompletedTask;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private ProtocolHttpResponse HandleHello(HelloRequest request)
    {
        var validationError = ValidateHello(request);
        if (validationError is not null)
            return Json(400, new { error = validationError });
        lock (_gate)
        {
            if (_helloAttempts.TryGetValue(request.MessageId, out var replayId) &&
                _sessions.TryGetValue(replayId, out var replay))
                return HelloResponse(replay);
        }

        _registry.Touch(Source, _options.FlushPeriodMilliseconds);
        var instance = ConvergeDesiredSpec();
        Session[] replaced;
        lock (_gate)
            replaced = _sessions.Values.ToArray();
        foreach (var old in replaced)
            StopAndRemove(old, ExternalHostActivationStopReason.LeaseReplaced);

        var activationId = Guid.CreateVersion7();
        var initialization = _runtime.BeginExternalHostActivation(
            instance.CollectorInstanceId,
            _package,
            request.ArtifactId,
            request.ArtifactHash,
            new ProtocolSupport(request.ProtocolMajors, request.SupportedCapabilities),
            activationId,
            request.MessageId);
        var session = new Session(
            activationId,
            request.MessageId,
            request.AppHint,
            initialization,
            null,
            null,
            _timeProvider.GetUtcNow() + _options.LeaseDuration);
        lock (_gate)
        {
            _sessions.Add(activationId, session);
            _helloAttempts[request.MessageId] = activationId;
        }
        return HelloResponse(session);
    }

    private ProtocolHttpResponse HandleReady(Guid activationId, ReadyRequest request)
    {
        var session = GetSession(activationId);
        if (session.Activation is not null)
            return ReadyResponse(session);
        if (!IsUuidV7(request.MessageId) || request.Bindings is null ||
            request.Bindings.Any(binding => binding is null || binding.Dimensions is null))
            return Json(400, new
            {
                error = Error("protocol_invalid_message", "ready messageId and bindings are malformed.")
            });
        if (request.Bindings.Any(binding => binding.Dimensions.ContainsKey("appHint")))
            return Json(400, new
            {
                error = Error(
                    "output_not_declared",
                    "appHint is supplied by the ExternalHost Binding and cannot be overridden by the Collector.")
            });
        var bindings = request.Bindings.Select(binding => new OutputBinding(
            binding.BindingId,
            binding.OutputId,
            binding.Dimensions
                .Append(new KeyValuePair<string, string>("appHint", session.AppHint))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))).ToArray();
        var activation = _runtime.ReadyExternalHostActivation(
            activationId,
            request.AppliedSpecRevision,
            bindings);
        var ready = session with
        {
            Activation = activation,
            LeaseToken = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
            ExpiresAt = _timeProvider.GetUtcNow() + _options.LeaseDuration
        };
        ReplaceSession(ready);
        RegisterPackageDeclaration();
        return ReadyResponse(ready);
    }

    private ProtocolHttpResponse HandleRenew(Guid activationId, RenewRequest request)
    {
        var session = GetSession(activationId);
        if (session.Activation is null || !FixedTimeEquals(session.LeaseToken, request.LeaseToken))
            return Json(409, new { error = Error("activation_stopping", "ExternalHost lease is not active.") });
        var renewed = session with { ExpiresAt = _timeProvider.GetUtcNow() + _options.LeaseDuration };
        ReplaceSession(renewed);
        return Json(200, LeaseBody(renewed));
    }

    private async ValueTask<ProtocolHttpResponse> HandleFactsAsync(
        Guid activationId,
        PublishRequest request,
        CancellationToken cancellationToken)
    {
        var session = GetSession(activationId);
        if (session.Activation is null || !FixedTimeEquals(session.LeaseToken, request.LeaseToken))
            return Json(409, new { error = Error("activation_stopping", "ExternalHost lease is not active.") });
        if (_registry.Snapshot.TryGetValue(Source, out var registration) && !registration.Enabled)
            return Json(403, new { error = Error("activation_stopping", "Browser Collector is disabled by Desired State.") });
        var acknowledgement = await session.Activation.PublishAsync(
            request.StreamId,
            request.MessageId,
            request.Facts,
            cancellationToken);
        if (acknowledgement.IsMessageRejected)
            return Json(400, new { error = acknowledgement.MessageError });
        return Json(200, new { results = acknowledgement.Results });
    }

    private async ValueTask<ProtocolHttpResponse> HandleGapAsync(
        Guid activationId,
        GapRequest request,
        CancellationToken cancellationToken)
    {
        var session = GetSession(activationId);
        if (session.Activation is null || !FixedTimeEquals(session.LeaseToken, request.LeaseToken))
            return Json(409, new { error = Error("activation_stopping", "ExternalHost lease is not active.") });
        var outcome = await session.Activation.ReportGapAsync(
            request.StreamId,
            request.MessageId,
            request.Gap,
            cancellationToken);
        return Json(outcome.Status == GapDeliveryStatus.Rejected ? 400 : 200, outcome);
    }

    private ProtocolHttpResponse HandleDrained(Guid activationId, DrainedRequest request)
    {
        var session = GetSession(activationId);
        if (session.Activation is null || !FixedTimeEquals(session.LeaseToken, request.LeaseToken))
            return Json(409, new { error = Error("activation_stopping", "ExternalHost lease is not active.") });
        if (request.PendingFacts < 0 || request.PendingGaps < 0)
            return Json(400, new { error = Error("protocol_invalid_message", "Pending counts must not be negative.") });
        StopAndRemove(session, ExternalHostActivationStopReason.CollectorDrained);
        return Json(200, new
        {
            appliedSpecRevision = request.AppliedSpecRevision,
            pendingFacts = request.PendingFacts,
            pendingGaps = request.PendingGaps,
            externalHostTerminated = false
        });
    }

    private CollectorInstance ConvergeDesiredSpec()
    {
        var enabled = !_registry.Snapshot.TryGetValue(Source, out var registration) || registration.Enabled;
        using var config = JsonDocument.Parse($$"""{"enabled":{{enabled.ToString().ToLowerInvariant()}},"flushPeriodMs":{{_options.FlushPeriodMilliseconds}}}""");
        var instance = _runtime.FindInstances(_package.Manifest.PackageId, _subject).SingleOrDefault();
        if (instance is null)
            return _runtime.CreateInstance(
                _package,
                _subject,
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        if (JsonElement.DeepEquals(instance.Spec.Config, config.RootElement))
            return instance;
        return _runtime.UpdateInstanceSpec(instance.CollectorInstanceId, 1, config.RootElement.Clone());
    }

    private void RegisterPackageDeclaration()
    {
        if (_package.ObservationDeclaration is not { } declaration)
            return;
        _registry.StoreDeclaration(Source, declaration.Json, declaration.Version);
    }

    private CollectorProtocolError? ValidateHello(HelloRequest request)
    {
        if (!IsUuidV7(request.MessageId) || string.IsNullOrWhiteSpace(request.AppHint) ||
            request.ProtocolMajors is null || request.SupportedCapabilities is null)
            return Error("protocol_invalid_message", "messageId, protocol support, and appHint are required.");
        var artifact = _package.Artifacts.SingleOrDefault(candidate => candidate.ArtifactId == request.ArtifactId);
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

    private Session GetSession(Guid activationId)
    {
        lock (_gate)
            return _sessions.TryGetValue(activationId, out var session)
                ? session
                : throw ActivationFailure("activation_stopping", "ExternalHost Activation was not found.");
    }

    private void ReplaceSession(Session session)
    {
        lock (_gate)
            _sessions[session.ActivationId] = session;
    }

    private void StopAndRemove(Session session, ExternalHostActivationStopReason reason)
    {
        lock (_gate)
        {
            _sessions.Remove(session.ActivationId);
            _helloAttempts.Remove(session.HelloMessageId);
        }
        if (session.Activation is null)
            _runtime.AbandonExternalHostActivation(session.ActivationId);
        else
            _runtime.StopExternalHostActivation(session.Activation, reason);
    }

    private ProtocolHttpResponse HelloResponse(Session session) => Json(200, new
    {
        activationId = session.ActivationId,
        selectedProtocolMajor = 1,
        selectedCapabilities = session.Initialization.SelectedCapabilities,
        spec = new
        {
            specRevision = session.Initialization.Spec.SpecRevision,
            configSchemaVersion = session.Initialization.Spec.ConfigSchemaVersion,
            config = session.Initialization.Spec.Config
        },
        limits = session.Initialization.Limits,
        readyWithinMs = (int)_options.LeaseDuration.TotalMilliseconds
    });

    private ProtocolHttpResponse ReadyResponse(Session session) => Json(200, new
    {
        streams = session.Activation!.Streams,
        lease = LeaseBody(session)
    });

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
        string AppHint,
        ExternalHostCollectorInitialization Initialization,
        ExternalHostCollectorActivation? Activation,
        string? LeaseToken,
        DateTimeOffset ExpiresAt);

    public sealed record HelloRequest(
        Guid MessageId,
        string ArtifactId,
        string ArtifactHash,
        IReadOnlyList<int> ProtocolMajors,
        IReadOnlyDictionary<string, IReadOnlyList<int>> SupportedCapabilities,
        string AppHint);

    public sealed record BindingRequest(
        string BindingId,
        string OutputId,
        IReadOnlyDictionary<string, string> Dimensions);

    public sealed record ReadyRequest(
        Guid MessageId,
        long AppliedSpecRevision,
        IReadOnlyList<BindingRequest> Bindings);

    public sealed record RenewRequest(string LeaseToken);

    public sealed record PublishRequest(
        Guid MessageId,
        string LeaseToken,
        Guid StreamId,
        IReadOnlyList<FactSubmission> Facts);

    public sealed record GapRequest(
        Guid MessageId,
        string LeaseToken,
        Guid StreamId,
        StreamGapReport Gap);

    public sealed record DrainedRequest(
        string LeaseToken,
        long AppliedSpecRevision,
        int PendingFacts,
        int PendingGaps);
}
