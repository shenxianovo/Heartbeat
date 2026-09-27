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
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);
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
        var records = new PresenceRecords(_target!, _interval * 2.5);
        var worldNames = new Dictionary<string, string?>(StringComparer.Ordinal);
        RecordSnapshot? pending = null;
        var backoff = _interval;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (pending is not null) { await SubmitAsync(pending, token); pending = null; }
                var presence = await _session!.GetPresenceAsync(token);
                var observedAt = DateTimeOffset.UtcNow;
                if (presence is not null)
                {
                    if (!worldNames.TryGetValue(presence.WorldId, out var name))
                    {
                        name = await _session.GetWorldNameAsync(presence.WorldId, token);
                        if (worldNames.Count >= 256) worldNames.Clear();
                        worldNames[presence.WorldId] = name;
                    }
                    presence = presence with { WorldName = name };
                }
                pending = records.Observe(presence, observedAt);
                if (pending is not null) { await SubmitAsync(pending, token); pending = null; }
                _error = null;
                backoff = _interval;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (VRChatUnauthorizedException)
            { _needsAuthentication = true; _error = "VRChat 会话已失效，请重新登录。"; break; }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                records.Break();
                _error = "VRChat 采样或交接失败，正在重试；缺失时间不会补成连续观测。";
                backoff = TimeSpan.FromSeconds(Math.Min(600, backoff.TotalSeconds * 2));
            }
            try { await Task.Delay(backoff, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
        if (pending is not null)
        {
            using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await SubmitAsync(pending, final.Token); }
            catch (Exception) { _error = "采集已停止，最后一份快照未能交给 Hub。"; }
        }
    }

    private Task SubmitAsync(RecordSnapshot record, CancellationToken token) => hub.SubmitAsync(new(
        new(VRChatCollectorFactory.Key, _target!, _displayName), new("vrchat.location", 1, "range", "explicit"), [record]), token);

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
