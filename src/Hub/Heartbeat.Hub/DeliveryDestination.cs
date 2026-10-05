namespace Heartbeat.Hub;

public sealed record DeliveryDestination
{
    public DeliveryDestination(Uri backendUrl, Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(backendUrl);
        if (!backendUrl.IsAbsoluteUri || backendUrl.Scheme is not ("http" or "https") ||
            backendUrl.AbsolutePath != "/" || backendUrl.Query.Length != 0 ||
            backendUrl.Fragment.Length != 0 || backendUrl.UserInfo.Length != 0 || ownerId == Guid.Empty)
        {
            throw new ArgumentException("A backend HTTP(S) origin and a nonempty Owner UUID are required.");
        }

        BackendUrl = backendUrl;
        OwnerId = ownerId;
    }

    public Uri BackendUrl { get; }

    public Guid OwnerId { get; }
}
