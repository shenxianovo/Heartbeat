using System.Security.Claims;

namespace Heartbeat.Api.Authentication;

public static class OwnerClaims
{
    public static bool TryGetOwnerId(ClaimsPrincipal? principal, out Guid ownerId)
    {
        var subject = principal?.FindFirst("sub")?.Value;
        return Guid.TryParse(subject, out ownerId) && ownerId != Guid.Empty;
    }
}
