using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Infrastructure.Database;

public sealed class HeartbeatDbContext(
    DbContextOptions<HeartbeatDbContext> options) : DbContext(options);
