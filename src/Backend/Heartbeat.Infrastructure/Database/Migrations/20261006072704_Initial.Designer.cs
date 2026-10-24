using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Infrastructure.Database.Migrations;

[DbContext(typeof(HeartbeatDbContext))]
[Migration("20261006072704_Initial")]
partial class Initial
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12");
}
