using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Heartbeat.Infrastructure.Database.Migrations;

[DbContext(typeof(HeartbeatDbContext))]
sealed partial class HeartbeatDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) =>
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12");
}
