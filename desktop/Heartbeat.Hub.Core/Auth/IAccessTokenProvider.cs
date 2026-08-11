namespace Heartbeat.Hub.Core.Auth
{
    public interface IAccessTokenProvider
    {
        Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
        void InvalidateToken();
    }
}
