using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

internal static class AppCatalogLock
{
    private const string LockName = "heartbeat.app-catalog";

    public static Task AcquireAsync(AppDbContext db, CancellationToken cancellationToken = default)
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({LockName}, 0))",
            cancellationToken);
}
