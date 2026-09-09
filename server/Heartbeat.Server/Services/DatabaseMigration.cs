using System.Diagnostics;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public static class DatabaseMigration
{
    public static async Task ApplyAsync(AppDbContext db, ILogger logger, int commandTimeoutSeconds = 900,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);
        var requestTimeout = db.Database.GetCommandTimeout();
        var elapsed = Stopwatch.StartNew();
        try
        {
            db.Database.SetCommandTimeout(commandTimeoutSeconds);
            logger.LogInformation("Database migration starting; command timeout {TimeoutSeconds}s. HTTP will listen after startup completes.",
                commandTimeoutSeconds);
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("Database migration completed in {ElapsedSeconds:F1}s.", elapsed.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed after {ElapsedSeconds:F1}s; Analytics cannot start.",
                elapsed.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            db.Database.SetCommandTimeout(requestTimeout);
        }
    }
}
