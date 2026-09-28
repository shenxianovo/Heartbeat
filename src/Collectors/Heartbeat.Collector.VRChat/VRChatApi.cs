using System.Diagnostics;
using System.Text.Json;
using VRChat.API.Client;

namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatAuthenticationState(string? DisplayName,
    IReadOnlyList<string> RequiredTwoFactorMethods, string? AccountId);

internal interface IVRChatApiSession
{
    Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken token);
    Task VerifyTwoFactorAsync(string method, string code, CancellationToken token);
    Task<VRChatPresenceSnapshot> GetSnapshotAsync(CancellationToken token);
    Task<IVRChatEventConnection> ConnectAsync(CancellationToken token);
    Task<string?> GetWorldNameAsync(string worldId, CancellationToken token);
    string ExportSession();
}

internal interface IVRChatApiFactory
{
    IVRChatApiSession FromCredentials(string username, string password);
    IVRChatApiSession FromSession(string serializedSession);
}

internal sealed class VRChatUnauthorizedException(string message) : Exception(message);
internal sealed class VRChatTransientException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class VRChatApiFactory(string applicationName, string applicationVersion,
    string applicationContact) : IVRChatApiFactory, IDisposable
{
    private readonly VRChatRequestGate _requests = new();
    public void Dispose() => _requests.Dispose();
    private VRChatClientBuilder Builder() => new VRChatClientBuilder()
        .WithApplication(applicationName, applicationVersion, applicationContact).WithTimeout(TimeSpan.FromSeconds(30));
    private VRChatApiSession Session(IVRChat client) => new(client, _requests,
        $"{applicationName}/{applicationVersion} {applicationContact}");
    public IVRChatApiSession FromCredentials(string username, string password) =>
        Session(Builder().WithUsername(username).WithPassword(password).Build());

    public IVRChatApiSession FromSession(string serializedSession)
    {
        var cookies = JsonSerializer.Deserialize<List<CookieRecord>>(serializedSession)
            ?? throw new JsonException("VRChat session is empty.");
        var auth = cookies.FirstOrDefault(cookie => cookie.Name == "auth")?.Value;
        if (string.IsNullOrWhiteSpace(auth)) throw new JsonException("VRChat session has no auth cookie.");
        return Session(Builder().WithAuthCookie(auth,
            cookies.FirstOrDefault(cookie => cookie.Name == "twoFactorAuth")?.Value ?? string.Empty).Build());
    }
}

internal sealed class VRChatApiSession(IVRChat client, VRChatRequestGate requests, string userAgent) : IVRChatApiSession
{
    private readonly DateTimeOffset _origin = DateTimeOffset.UtcNow;
    private readonly long _started = Stopwatch.GetTimestamp();
    private DateTimeOffset Now() => _origin + Stopwatch.GetElapsedTime(_started);

    public async Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken token)
    {
        var user = await requests.RunAsync(() => client.Authentication.GetCurrentUserAsync(token), token);
        return new(user.DisplayName, user.RequiresTwoFactorAuth?.ToArray() ?? [], user.Id);
    }

    public async Task VerifyTwoFactorAsync(string method, string code, CancellationToken token)
    {
        if (method == "emailOtp")
        {
            var result = await requests.RunAsync(() => client.Authentication.Verify2FAEmailCodeAsync(
                new global::VRChat.API.Model.TwoFactorEmailCode(code), token), token);
            if (!result.Verified) throw new VRChatUnauthorizedException("VRChat 验证码未通过。");
        }
        else
        {
            var result = await requests.RunAsync(() => client.Authentication.Verify2FAAsync(
                new global::VRChat.API.Model.TwoFactorAuthCode(code), token), token);
            if (!result.Verified) throw new VRChatUnauthorizedException("VRChat 验证码未通过。");
        }
    }

    public async Task<VRChatPresenceSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        var requested = Now();
        var current = await requests.RunAsync(() => client.Authentication.GetCurrentUserAsync(token), token);
        if (current.RequiresTwoFactorAuth?.Count > 0) throw new VRChatUnauthorizedException("VRChat 需要重新认证。");
        var own = VRChatLocation.Parse($"{current.Presence?.World}:{current.Presence?.Instance}");
        List<VRChatPresenceUpdate> users = [new(current.Id, current.DisplayName, own, Now(), "snapshot")];
        for (var offset = 0; ; offset += 100)
        {
            var friends = await requests.RunAsync(() => client.Friends.GetFriendsAsync(100, offset, false, token), token);
            users.AddRange(friends.Select(friend => new VRChatPresenceUpdate(friend.Id, friend.DisplayName,
                VRChatLocation.Parse(friend.Location), Now(), "snapshot")));
            if (friends.Count < 100) break;
        }
        return new(requested, users);
    }

    public Task<IVRChatEventConnection> ConnectAsync(CancellationToken token)
    {
        var auth = client.GetCookies().FirstOrDefault(cookie => cookie.Name == "auth")?.Value;
        if (string.IsNullOrEmpty(auth)) throw new VRChatUnauthorizedException("VRChat 会话缺失。");
        return VRChatPipeline.ConnectAsync(auth, userAgent, Now, token);
    }

    public async Task<string?> GetWorldNameAsync(string worldId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { return (await requests.RunAsync(() => client.Worlds.GetWorldAsync(worldId, timeout.Token), timeout.Token)).Name; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is VRChatTransientException or VRChatUnauthorizedException) { return null; } // Cosmetic failure; shared gate still enforces 429 cooldown.
    }

    public string ExportSession() => JsonSerializer.Serialize(
        client.GetCookies().Select(cookie => new CookieRecord(cookie.Name, cookie.Value)));
}

internal sealed record CookieRecord(string Name, string Value);
