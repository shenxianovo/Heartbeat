using System.Text.Json;
using Heartbeat.Hub;
using Heartbeat.Hub.Runtime;
using Heartbeat.Management;

namespace Heartbeat.Collector.VRChat;

internal sealed class VRChatCollector(string? expectedTarget, IVRChatApiFactory api, IHubSubmissionClient hub,
    LocalSecretStore secrets) : ICollectorSession
{
    private string? _target = expectedTarget;
    private IVRChatApiSession? _session;
    private IReadOnlyList<string> _twoFactor = [];
    private CancellationTokenSource? _stop;
    private Task? _run;
    private string _displayName = "VRChat";
    private string? _error;
    private bool _needsAuthentication = true;

    public CollectorState? State => _target is null ? null : new(VRChatCollectorFactory.Key, _target, _displayName,
        _needsAuthentication ? "authentication_required" : _error is not null ? "error" : _run is { IsCompleted: false } ? "running" : "paused", _error);

    public async Task<CollectorLoginResult> LoginAsync(JsonElement input, CancellationToken token)
    {
        if (Text(input, "username") is { } username && Text(input, "password") is { } password)
        {
            _session = api.FromCredentials(username, password);
            _twoFactor = [];
        }
        if (_session is null) return Prompt("请输入 VRChat 用户名和密码。");
        try
        {
            if (_twoFactor.Count > 0)
            {
                var code = Text(input, "code");
                if (code is null) return Prompt();
                var method = _twoFactor.Contains("emailOtp") ? "emailOtp" : _twoFactor.Contains("totp") ? "totp" : null;
                if (method is null) return Prompt("此账号要求的两步验证方式尚不支持。");
                await _session.VerifyTwoFactorAsync(method, code, token);
            }
            var state = await _session.AuthenticateAsync(token);
            _twoFactor = state.RequiredTwoFactorMethods;
            if (_twoFactor.Count > 0) return Prompt();
            BindAccount(state);
            var stored = _session.ExportSession();
            secrets.Write(SecretName(_target!), stored);
            _session = api.FromSession(stored);
            _needsAuthentication = false;
            _error = null;
            return new(_target, []);
        }
        catch (VRChatUnauthorizedException) { return Prompt("VRChat 认证失败，请检查登录信息或验证码。"); }
        catch (VRChatTransientException) { return Prompt("VRChat 暂时无法连接，请稍后重试。"); }
    }

    private CollectorLoginResult Prompt(string? error = null) => new(null,
        _twoFactor.Count > 0 ? [new("code", "两步验证码", "secret", true)] : VRChatCollectorFactory.LoginFields, error);

    private void BindAccount(VRChatAuthenticationState state)
    {
        var account = state.AccountId;
        if (account is null || !account.StartsWith("usr_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(account[4..], "D", out var id) || account != $"usr_{id:D}")
            throw new InvalidOperationException("VRChat 未返回有效账号身份。");
        if (_target is not null && account != _target)
            throw new InvalidOperationException("登录账号与原 Collector 不一致。");
        _target = account;
        _displayName = state.DisplayName ?? "VRChat";
    }

    public async Task RestoreAsync(CancellationToken token)
    {
        if (_target is null) throw new InvalidOperationException("Cannot restore an unidentified account.");
        try
        {
            if (secrets.Read(SecretName(_target)) is not { } saved) { _error = "请登录 VRChat。"; return; }
            _session = api.FromSession(saved);
            var state = await _session.AuthenticateAsync(token);
            if (state.RequiredTwoFactorMethods.Count > 0) { _error = "VRChat 会话已失效，请重新登录。"; return; }
            BindAccount(state);
            _needsAuthentication = false;
            await StartAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (VRChatTransientException)
        {
            // A saved session is not invalidated by a temporary network/429 failure.
            // The stream bootstrap verifies its account before producing any Record.
            _needsAuthentication = false;
            await StartAsync(token);
        }
        catch (Exception)
        { _needsAuthentication = true; _error = "无法恢复 VRChat 会话，请重新登录。"; }
    }

    private static string SecretName(string target) => $"vrchat:{target}:session";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_run is { IsCompleted: false }) return Task.CompletedTask;
        if (_session is null || _needsAuthentication || _target is null) throw new InvalidOperationException("请先完成 VRChat 认证。");
        _stop?.Dispose();
        _stop = new CancellationTokenSource();
        _error = null;
        _run = PollAsync(_stop.Token);
        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken token)
    {
        var pending = new PendingHubSubmissions();
        var collector = new CollectorDeclaration(VRChatCollectorFactory.Key, _target!, _displayName);
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        var backoff = 30d;
        while (!token.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                await FlushAsync(pending, token);
                var records = new PresenceRecords(_target!);
                await VRChatFeed.RunAsync(_session!, async item =>
                {
                    ValidateAccount(item);
                    var name = await ResolveWorldAsync(records.WorldToResolve(item), names, token);
                    foreach (var record in records.Observe(item, name))
                        pending.Stage(new(collector, new(record.Type, 1, "range", "explicit")), record.Record);
                    await FlushAsync(pending, token);
                    _error = null;
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (VRChatUnauthorizedException)
            { _needsAuthentication = true; _error = "VRChat 会话已失效，请重新登录。"; break; }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(5)) backoff = 30;
                var delay = RetryDelay(exception, backoff);
                backoff = Math.Min(600, backoff * 2);
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await FlushAsync(pending, final.Token); }
        catch (Exception) { _error = "采集已停止，最后一份观测未能交给 Hub。"; }
    }

    private void ValidateAccount(VRChatFeedItem item)
    {
        if (item.Snapshot is { } snapshot && snapshot.Users[0].AccountId != _target)
            throw new VRChatUnauthorizedException("VRChat 账号不匹配。");
        if (item.Event is { Cause: "user-location" } update && update.AccountId != _target)
            throw new VRChatUnauthorizedException("VRChat 账号不匹配。");
    }

    private async Task<string?> ResolveWorldAsync(string? world, Dictionary<string, string?> names, CancellationToken token)
    {
        if (world is null) return null;
        if (names.TryGetValue(world, out var cached)) return cached;
        var name = await _session!.GetWorldNameAsync(world, token);
        if (names.Count >= 256) names.Clear();
        if (name is not null) names[world] = name;
        return name;
    }

    private TimeSpan RetryDelay(Exception exception, double backoff)
    {
        var retry = exception is VRChatTransientException transient ? transient.RetryAfter : null;
        var delay = TimeSpan.FromSeconds(backoff + Random.Shared.NextDouble() * backoff * 0.2);
        if (retry > delay) delay = retry.Value;
        var reason = exception switch
        {
            VRChatTransientException known => known.Message,
            VRChatHandoffException => "交给 Hub 失败",
            _ => "VRChat 事件接收或解析失败",
        };
        _error = $"{reason}；将在 {Math.Ceiling(delay.TotalSeconds)} 秒后重连，缺失时间保留为空白。";
        return delay;
    }

    private async Task FlushAsync(PendingHubSubmissions pending, CancellationToken token)
    {
        try
        {
            foreach (var batch in pending.ReadBatches())
            {
                await hub.SubmitAsync(batch.ToSubmission(), token);
                pending.Confirm(batch);
            }
        }
        catch (Exception) when (!token.IsCancellationRequested) { throw new VRChatHandoffException(); }
    }

    private async Task PauseAsync(CancellationToken cancellationToken)
    {
        if (_stop is null) return;
        await _stop.CancelAsync();
        if (_run is not null) await _run;
        _stop.Dispose();
        _stop = null;
        _run = null;
    }
    public async ValueTask DisposeAsync() => await PauseAsync(CancellationToken.None);
    private static string? Text(JsonElement input, string key) => input.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}

internal sealed class VRChatHandoffException : Exception;
