using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlSystem.Server.Data.Migrations;

[DbContext(typeof(MonitoringDbContext))]
[Migration("202611220001_InitialMonitoring")]
public sealed class InitialMonitoring : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Devices", columns: table => new
        {
            DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
            Hostname = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
            OperatingSystem = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
            AgentVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
            SampledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
            TelemetryJson = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Devices", d => d.DeviceId));
        migrationBuilder.CreateIndex("IX_Devices_LastSeenAtUtc", "Devices", "LastSeenAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Devices");

    protected override void BuildTargetModel(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder) =>
        MonitoringModel.Configure(modelBuilder);
}
