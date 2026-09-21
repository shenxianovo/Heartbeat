using System.Text.Json;
using Heartbeat.Hub;
using Heartbeat.Hub.Runtime;
using Heartbeat.Management;

namespace Heartbeat.Collector.VRChat;

internal sealed class VRChatCollector(string target, IVRChatApiFactory api, IHubSubmissionClient hub,
    LocalSecretStore secrets) : IManagedCollector
{
    private IVRChatApiSession? _session;
    private IReadOnlyList<string> _twoFactor = [];
    private CancellationTokenSource? _stop;
    private Task? _run;
    private string _displayName = "VRChat";
    private string? _error;
    private bool _needsAuthentication;
    private TimeSpan _interval = TimeSpan.FromMinutes(1);
    private readonly string _secretName = $"vrchat:{target}:session";
    public CollectorState State => new(VRChatCollectorFactory.Key, target, _displayName,
        _needsAuthentication ? "authentication_required" : _error is not null ? "error" : _run is { IsCompleted: false } ? "running" : "paused", _error);

    public async Task<JsonElement> ConfigureAsync(JsonElement config, CancellationToken token)
    {
        _displayName = Text(config, "displayName") ?? _displayName;
        ConfigureInterval(config);
        var password = Text(config, "password");
        if (password is not null)
        {
            var username = Text(config, "username") ?? throw new ArgumentException("请输入 VRChat 用户名。");
            _session = api.FromCredentials(username, password);
            _twoFactor = [];
        }
        else if (_session is null && secrets.Read(_secretName) is { } stored) _session = api.FromSession(stored);
        if (_session is not null) await AuthorizeAsync(Text(config, "code"), token);
        else { _needsAuthentication = true; _error = "请填写 VRChat 登录信息。"; }
        return JsonSerializer.SerializeToElement(new { displayName = _displayName, pollIntervalSeconds = (int)_interval.TotalSeconds });
    }

    private void ConfigureInterval(JsonElement config)
    {
        if (!config.TryGetProperty("pollIntervalSeconds", out var interval) || interval.ValueKind == JsonValueKind.Null) return;
        if (!interval.TryGetInt32(out var seconds) || seconds is < 60 or > 3600)
            throw new ArgumentException("采样间隔须为 60 到 3600 秒。");
        _interval = TimeSpan.FromSeconds(seconds);
    }

    private async Task AuthorizeAsync(string? code, CancellationToken token)
    {
        _needsAuthentication = true;
        try
        {
            if (code is not null && _twoFactor.Count > 0)
            {
                var method = _twoFactor.Contains("emailOtp") ? "emailOtp" : _twoFactor.Contains("totp") ? "totp" : null;
                if (method is null) throw new InvalidOperationException("此账号要求的两步验证方式尚不支持。");
                await _session!.VerifyTwoFactorAsync(method, code, token);
            }
            var state = await _session!.AuthenticateAsync(token);
            _twoFactor = state.RequiredTwoFactorMethods;
            _needsAuthentication = _twoFactor.Count > 0;
            _error = _needsAuthentication ? "需要两步验证，请填写验证码后再次保存。" : null;
            if (_needsAuthentication) return;
            if (state.AccountId != target)
            {
                _session = null;
                _needsAuthentication = true;
                throw new InvalidOperationException("登录账号与填写的 VRChat 用户 ID 不一致。");
            }
            // A new SDK client keeps the session cookies, but no longer retains the login password.
            var stored = _session.ExportSession();
            secrets.Write(_secretName, stored);
            _session = api.FromSession(stored);
        }
        catch (VRChatUnauthorizedException)
        { _needsAuthentication = true; _error = "VRChat 认证失败，请重新登录或检查验证码。"; }
        catch (VRChatTransientException)
        { _error = "VRChat 暂时无法连接，请稍后保存重试。"; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_run is { IsCompleted: false }) return Task.CompletedTask;
        if (_session is null || _needsAuthentication) throw new InvalidOperationException("请先完成 VRChat 认证。");
        _stop?.Dispose();
        _stop = new CancellationTokenSource();
        _error = null;
        _run = PollAsync(_stop.Token);
        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken token)
    {
        var records = new PresenceRecords(target, _interval * 2.5);
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
        new(VRChatCollectorFactory.Key, target, _displayName), new("vrchat.location", 1, "range", "explicit"), [record]), token);

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        if (_stop is null) return;
        await _stop.CancelAsync();
        if (_run is not null) await _run;
        _stop.Dispose();
        _stop = null;
        _run = null;
    }
    public async ValueTask DisposeAsync() => await PauseAsync(CancellationToken.None);
    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        await PauseAsync(cancellationToken);
        secrets.Delete(_secretName);
        _session = null;
    }
    private static string? Text(JsonElement config, string key) => config.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}
