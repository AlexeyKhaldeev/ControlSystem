using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlSystem.Server.Data.Migrations;

[DbContext(typeof(MonitoringDbContext))]
[Migration("202611270001_TelemetryHistory")]
public sealed class TelemetryHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("TelemetryHistory", columns: table => new
        {
            Id = table.Column<long>(type: "bigint", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
            Hostname = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
            OperatingSystem = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
            AgentVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
            SampledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
            TelemetryJson = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_TelemetryHistory", d => d.Id));
        migrationBuilder.CreateIndex("IX_TelemetryHistory_DeviceId_LastSeenAtUtc_Id", "TelemetryHistory", new[] { "DeviceId", "LastSeenAtUtc", "Id" });
        migrationBuilder.CreateIndex("IX_TelemetryHistory_LastSeenAtUtc_Id", "TelemetryHistory", new[] { "LastSeenAtUtc", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("TelemetryHistory");

    protected override void BuildTargetModel(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
    {
        MonitoringModel.Configure(modelBuilder);
        HistoryModel.Configure(modelBuilder);
    }
}
