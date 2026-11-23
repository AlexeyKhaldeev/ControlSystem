using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ControlSystem.Server.Monitoring;

namespace ControlSystem.Server.Data.Migrations;

[DbContext(typeof(MonitoringDbContext))]
public sealed class MonitoringDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => MonitoringModel.Configure(modelBuilder);
}

internal static class MonitoringModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");
        var entity = modelBuilder.Entity<DeviceSnapshot>();
        entity.Property(d => d.DeviceId).HasColumnType("uuid").ValueGeneratedNever();
        entity.Property(d => d.Hostname).HasColumnType("character varying(120)").HasMaxLength(120).IsRequired();
        entity.Property(d => d.OperatingSystem).HasColumnType("character varying(200)").HasMaxLength(200).IsRequired();
        entity.Property(d => d.AgentVersion).HasColumnType("character varying(40)").HasMaxLength(40).IsRequired();
        entity.Property(d => d.SampledAtUtc).HasColumnType("timestamp with time zone");
        entity.Property(d => d.LastSeenAtUtc).HasColumnType("timestamp with time zone");
        entity.Property(d => d.IsSimulated).HasColumnType("boolean");
        entity.Property(d => d.TelemetryJson).HasColumnType("text").IsRequired();
        entity.HasKey(d => d.DeviceId);
        entity.HasIndex(d => d.LastSeenAtUtc);
        entity.ToTable("Devices");
    }
}
