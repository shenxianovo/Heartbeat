// Adapted from main: VRChat API/session adapter; independent of the old CollectorProtocol.
using System.Net;
using System.Text.Json;
using VRChat.API.Client;

namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatAuthenticationState(
    string? DisplayName,
    IReadOnlyList<string> RequiredTwoFactorMethods,
    string? AccountId);

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
            var user = await AuthenticateCurrentUserAsync(cancellationToken);
            return new VRChatAuthenticationState(user.DisplayName,
                user.RequiresTwoFactorAuth?.ToArray() ?? [], user.Id);
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
            return new VRChatPresence(worldId, null, instanceId, user.Id);
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
