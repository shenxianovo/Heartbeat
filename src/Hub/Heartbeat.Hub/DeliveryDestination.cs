using System.Text.Json;

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

    public void CheckTokenOwner(string token)
    {
        // This is a routing guard, not authentication. The backend must verify the JWT signature,
        // issuer, audience and expiry. A forged sub cannot authorize backend writes.
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                throw new FormatException();
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!json.RootElement.TryGetProperty("sub", out var sub) ||
                sub.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(sub.GetString(), out var owner) || owner != OwnerId)
            {
                throw new FormatException();
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The backend token does not match the queue Owner.", nameof(token), exception);
        }
    }
}
