using System.Net;
using System.Text.Json;
using VRChat.API.Client;

namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatAuthenticationState(
    string? DisplayName,
    IReadOnlyList<string> RequiredTwoFactorMethods);

internal interface IVRChatApiSession
{
    Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken cancellationToken);
    Task VerifyTwoFactorAsync(string method, string code, CancellationToken cancellationToken);
    Task<VRChatPresence?> GetPresenceAsync(CancellationToken cancellationToken);
    Task<string?> GetWorldNameAsync(string worldId, CancellationToken cancellationToken);
    string ExportSession();
}

internal interface IVRChatApiFactory
{
    IVRChatApiSession FromCredentials(string username, string password);
    IVRChatApiSession FromSession(string serializedSession);
}

internal sealed class VRChatUnauthorizedException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class VRChatTransientException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class VRChatApiFactory(
    string applicationName,
    string applicationVersion,
    string applicationContact) : IVRChatApiFactory
{
    public IVRChatApiSession FromCredentials(string username, string password) =>
        new VRChatApiSession(new VRChatClientBuilder()
            .WithUsername(username)
            .WithPassword(password)
            .WithApplication(applicationName, applicationVersion, applicationContact)
            .Build());

    public IVRChatApiSession FromSession(string serializedSession)
    {
        var cookies = JsonSerializer.Deserialize<List<CookieRecord>>(serializedSession)
            ?? throw new JsonException("VRChat session cookie document is empty.");
        var auth = cookies.FirstOrDefault(cookie => cookie.Name == "auth")?.Value;
        var twoFactor = cookies.FirstOrDefault(cookie => cookie.Name == "twoFactorAuth")?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(auth))
            throw new JsonException("VRChat session cookie document has no auth cookie.");
        return new VRChatApiSession(new VRChatClientBuilder()
            .WithAuthCookie(auth, twoFactor)
            .WithApplication(applicationName, applicationVersion, applicationContact)
            .Build());
    }

    private sealed class VRChatApiSession(IVRChat client) : IVRChatApiSession
    {
        public async Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken cancellationToken)
        {
            try
            {
                var user = await client.Authentication.GetCurrentUserAsync(cancellationToken);
                return new VRChatAuthenticationState(
                    user.DisplayName,
                    user.RequiresTwoFactorAuth?.ToArray() ?? []);
            }
            catch (ApiException exception) when (exception.ErrorCode == (int)HttpStatusCode.Unauthorized)
            {
                throw new VRChatUnauthorizedException("VRChat rejected the current session.", exception);
            }
            catch (ApiException exception) when (IsTransient(exception))
            {
                throw new VRChatTransientException("VRChat authentication is temporarily unavailable.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new VRChatTransientException("VRChat authentication network request failed.", exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new VRChatTransientException("VRChat authentication request timed out.", exception);
            }
        }

        public async Task VerifyTwoFactorAsync(
            string method,
            string code,
            CancellationToken cancellationToken)
        {
            try
            {
                if (method == "emailOtp")
                {
                    await client.Authentication.Verify2FAEmailCodeAsync(
                        new global::VRChat.API.Model.TwoFactorEmailCode(code),
                        cancellationToken);
                }
                else
                {
                    await client.Authentication.Verify2FAAsync(
                        new global::VRChat.API.Model.TwoFactorAuthCode(code),
                        cancellationToken);
                }
            }
            catch (ApiException exception) when (exception.ErrorCode == (int)HttpStatusCode.Unauthorized)
            {
                throw new VRChatUnauthorizedException("VRChat rejected the verification code.", exception);
            }
            catch (ApiException exception) when (IsTransient(exception))
            {
                throw new VRChatTransientException("VRChat verification is temporarily unavailable.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new VRChatTransientException("VRChat verification network request failed.", exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new VRChatTransientException("VRChat verification request timed out.", exception);
            }
        }

        public async Task<VRChatPresence?> GetPresenceAsync(CancellationToken cancellationToken)
        {
            var user = await AuthenticateCurrentUserAsync(cancellationToken);
            var worldId = user.Presence?.World;
            var instanceId = user.Presence?.Instance;
            if (string.IsNullOrWhiteSpace(worldId) ||
                worldId is "offline" or "private" ||
                string.IsNullOrWhiteSpace(instanceId))
                return null;
            return new VRChatPresence(worldId, null, instanceId);
        }

        public async Task<string?> GetWorldNameAsync(string worldId, CancellationToken cancellationToken)
        {
            try
            {
                return (await client.Worlds.GetWorldAsync(worldId, cancellationToken)).Name;
            }
            catch (Exception exception) when (
                exception is ApiException or HttpRequestException ||
                exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        public string ExportSession() => JsonSerializer.Serialize(
            client.GetCookies().Select(cookie => new CookieRecord(cookie.Name, cookie.Value)));

        private async Task<global::VRChat.API.Model.CurrentUser> AuthenticateCurrentUserAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await client.Authentication.GetCurrentUserAsync(cancellationToken);
            }
            catch (ApiException exception) when (exception.ErrorCode == (int)HttpStatusCode.Unauthorized)
            {
                throw new VRChatUnauthorizedException("VRChat rejected the current session.", exception);
            }
            catch (ApiException exception) when (IsTransient(exception))
            {
                throw new VRChatTransientException("VRChat presence is temporarily unavailable.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new VRChatTransientException("VRChat presence network request failed.", exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new VRChatTransientException("VRChat presence request timed out.", exception);
            }
        }

        private static bool IsTransient(ApiException exception) =>
            exception.ErrorCode == (int)HttpStatusCode.TooManyRequests || exception.ErrorCode >= 500;
    }
}

internal sealed record CookieRecord(string Name, string Value);

internal sealed class MockVRChatApiFactory(int transientPresenceFailures = 0) : IVRChatApiFactory
{
    private int _remainingTransientPresenceFailures = transientPresenceFailures;

    public IVRChatApiSession FromCredentials(string username, string password) =>
        new MockVRChatApiSession(this, hasSession: false, username, password);

    public IVRChatApiSession FromSession(string serializedSession)
    {
        var cookies = JsonSerializer.Deserialize<List<CookieRecord>>(serializedSession);
        if (cookies?.Any(cookie => cookie is { Name: "auth", Value: "mock-auth" }) != true)
            throw new VRChatUnauthorizedException("Mock session is invalid.");
        return new MockVRChatApiSession(this, hasSession: true, string.Empty, string.Empty);
    }

    private sealed class MockVRChatApiSession : IVRChatApiSession
    {
        private readonly MockVRChatApiFactory _owner;
        private readonly bool _hasSession;
        private readonly string _username;
        private readonly string _password;
        private bool _verified;

        public MockVRChatApiSession(
            MockVRChatApiFactory owner,
            bool hasSession,
            string username,
            string password)
        {
            _owner = owner;
            _hasSession = hasSession;
            _username = username;
            _password = password;
            _verified = hasSession;
        }

        public Task<VRChatAuthenticationState> AuthenticateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_hasSession && (_username != "test-user" || _password != "test-password"))
                throw new VRChatUnauthorizedException("Mock credentials are invalid.");
            return Task.FromResult(new VRChatAuthenticationState(
                _verified ? "Mock VRChat User" : null,
                _verified ? [] : ["emailOtp"]));
        }

        public Task VerifyTwoFactorAsync(string method, string code, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method != "emailOtp" || code != "123456")
                throw new VRChatUnauthorizedException("Mock verification code is invalid.");
            _verified = true;
            return Task.CompletedTask;
        }

        public Task<VRChatPresence?> GetPresenceAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_verified)
                throw new VRChatUnauthorizedException("Mock session is not verified.");
            if (Interlocked.Decrement(ref _owner._remainingTransientPresenceFailures) >= 0)
                throw new VRChatTransientException("Mock transient presence failure.");
            return Task.FromResult<VRChatPresence?>(
                new VRChatPresence("wrld_mock", null, "instance:mock"));
        }

        public Task<string?> GetWorldNameAsync(string worldId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(worldId == "wrld_mock" ? "Mock World" : null);

        public string ExportSession() => JsonSerializer.Serialize(new[]
        {
            new CookieRecord("auth", "mock-auth"),
            new CookieRecord("twoFactorAuth", "mock-two-factor")
        });
    }
}
