namespace Heartbeat.Collection.Hub.Auth
{
    public interface IAccessTokenProvider
    {
        Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
        void InvalidateToken();
    }
}
