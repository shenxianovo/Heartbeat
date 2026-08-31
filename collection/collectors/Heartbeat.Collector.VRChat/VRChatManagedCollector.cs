using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.CollectorProtocol;

namespace Heartbeat.Collector.VRChat;

internal sealed class VRChatManagedCollector(
    IVRChatApiFactory apiFactory,
    Func<DateTimeOffset>? clock = null) : ICollectorProtocolApplication
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private TimeSpan _pollInterval = TimeSpan.FromMinutes(1);
    private IVRChatApiSession? _session;
    private PresenceStateMachine _presence = new();
    private VRChatPresenceCheckpoint? _checkpoint;
    private CollectorActivation? _activation;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;

    public async ValueTask InitializeAsync(
        CollectorActivation activation,
        CancellationToken cancellationToken)
    {
        _activation = activation;
        var config = activation.Initialization.Config;
        if (config.TryGetProperty("pollIntervalSeconds", out var interval) &&
            interval.TryGetInt32(out var seconds) && seconds > 0)
            _pollInterval = TimeSpan.FromSeconds(seconds);
        _checkpoint = VRChatPresenceCheckpoint.Open(
            Path.Combine(activation.Initialization.DataDirectory, "vrchat-presence.json"),
            _clock());
        await PublishPendingAsync(cancellationToken);
        if (_checkpoint.Active is { } active)
        {
            _presence.Restore(active);
            var recoveredAt = _clock();
            var finalized = _presence.FinalizeRestored();
            var gaps = recoveredAt > active.End
                ? new[]
                {
                    new VRChatPresenceRecoveryGap(
                        Guid.CreateVersion7(),
                        active.End,
                        recoveredAt,
                        "process_restart")
                }
                : [];
            await PublishAsync([finalized], gaps, cancellationToken);
        }
        await EnsureAuthorizedAsync(cancellationToken);
    }

    public ValueTask StartAsync(
        CollectorActivation activation,
        CancellationToken cancellationToken)
    {
        if (_pollTask is not null)
            throw new InvalidOperationException("VRChat presence polling is already running.");
        _pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = Task.Run(() => PollAsync(_pollCancellation.Token), CancellationToken.None);
        return new ValueTask(_pollTask);
    }

    public async ValueTask StopAsync(
        CollectorDrainContext drain,
        CancellationToken cancellationToken)
    {
        if (_pollCancellation is not null)
        {
            await _pollCancellation.CancelAsync();
            if (_pollTask is not null)
            {
                try
                {
                    await _pollTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected while draining.
                }
            }
            _pollCancellation.Dispose();
            _pollCancellation = null;
            _pollTask = null;
        }
        var facts = _presence.Stop(_clock());
        _checkpoint!.Stage(facts, []);
        await PublishPendingAsync(drain, cancellationToken);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await SynchronizePresenceAsync(cancellationToken);
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await SynchronizePresenceAsync(cancellationToken);
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
            await PublishAsync(_presence.Observe(observed, _clock()), cancellationToken);
        }
        catch (VRChatUnauthorizedException)
        {
            await PublishAsync(_presence.Stop(_clock()), cancellationToken);
            await _activation!.DeleteSecretAsync("session", cancellationToken);
            await EnsureAuthorizedAsync(cancellationToken);
        }
        catch (VRChatTransientException)
        {
            // Preserve the active segment. The next poll retries without inventing a transition.
        }
    }

    private async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        var saved = await _activation!.ReadSecretAsync("session", cancellationToken);
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
                await _activation.DeleteSecretAsync("session", cancellationToken);
            }
        }

        string? error = null;
        while (true)
        {
            var credentials = await _activation.ChallengeAsync(
                "credentials",
                "登录 VRChat",
                error,
                [
                    new CollectorAuthorizationField("username", "用户名或邮箱", false, "text"),
                    new CollectorAuthorizationField("password", "密码", true, "text")
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
                    var verification = await _activation.ChallengeAsync(
                        "verificationCode",
                        method == "emailOtp" ? "输入邮件验证码" : "输入两步验证码",
                        null,
                        [new CollectorAuthorizationField("code", "验证码", true, "numeric")],
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
                await _activation.WriteSecretAsync("session", candidate.ExportSession(), cancellationToken);
                _session = candidate;
                await _activation.CompleteAuthorizationAsync(lastInteractionId, cancellationToken);
                return;
            }
            catch (VRChatUnauthorizedException)
            {
                error = "登录失败，请检查账号信息后重试。";
            }
        }
    }

    private async Task PublishAsync(
        IReadOnlyList<VRChatPresenceFact> facts,
        CancellationToken cancellationToken)
    {
        await PublishAsync(facts, [], cancellationToken);
    }

    private async Task PublishAsync(
        IReadOnlyList<VRChatPresenceFact> facts,
        IReadOnlyList<VRChatPresenceRecoveryGap> gaps,
        CancellationToken cancellationToken)
    {
        if (facts.Count == 0 && gaps.Count == 0)
            return;
        _checkpoint!.Stage(facts, gaps);
        await PublishPendingAsync(cancellationToken);
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        while (_checkpoint!.PendingFacts.Count != 0)
        {
            var fact = _checkpoint.PendingFacts[0];
            await _activation!.PublishAsync(ToFact(fact), cancellationToken);
            _checkpoint.Acknowledge(fact);
        }
        while (_checkpoint.PendingGaps.Count != 0)
        {
            var gap = _checkpoint.PendingGaps[0];
            await _activation!.ReportGapAsync(ToGap(gap), cancellationToken);
            _checkpoint.Acknowledge(gap);
        }
    }

    private async Task PublishPendingAsync(
        CollectorDrainContext drain,
        CancellationToken cancellationToken)
    {
        while (_checkpoint!.PendingFacts.Count != 0)
        {
            var fact = _checkpoint.PendingFacts[0];
            await drain.PublishAsync(ToFact(fact), cancellationToken);
            _checkpoint.Acknowledge(fact);
        }
        while (_checkpoint.PendingGaps.Count != 0)
        {
            var gap = _checkpoint.PendingGaps[0];
            await drain.ReportGapAsync(ToGap(gap), cancellationToken);
            _checkpoint.Acknowledge(gap);
        }
    }

    internal static CollectorFact ToFact(VRChatPresenceFact fact) => new(
        "presence",
        1,
        fact.FactId,
        fact.Revision,
        fact.End,
        CollectorFactRecordState.Present,
        new CollectorSegmentFactTime(fact.Start, fact.End, fact.IsFinal),
        JsonSerializer.SerializeToElement(new
        {
            identityKey = fact.IdentityKey,
            title = fact.Title,
            appDisplayName = "VRChat",
            worldId = fact.WorldId,
            worldName = fact.WorldName,
            instanceId = fact.InstanceId
        }, PayloadJsonOptions));

    private static CollectorStreamGap ToGap(VRChatPresenceRecoveryGap gap) => new(
        gap.GapId,
        "presence",
        gap.Start,
        gap.End,
        gap.Reason);

    private static async Task<T> RetryTransientAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                return await operation(cancellationToken);
            }
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
}
