using ControlSystem.Server.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace ControlSystem.Server.Data;

public sealed class MonitoringDbContext(DbContextOptions<MonitoringDbContext> options) : DbContext(options)
{
    public DbSet<DeviceSnapshot> Devices => Set<DeviceSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DeviceSnapshot>();
        entity.ToTable("Devices");
        entity.HasKey(d => d.DeviceId);
        entity.Property(d => d.DeviceId).ValueGeneratedNever();
        entity.Property(d => d.Hostname).HasMaxLength(120).IsRequired();
        entity.Property(d => d.OperatingSystem).HasMaxLength(200).IsRequired();
        entity.Property(d => d.AgentVersion).HasMaxLength(40).IsRequired();
        entity.Property(d => d.TelemetryJson).HasColumnType("text").IsRequired();
        entity.HasIndex(d => d.LastSeenAtUtc);
    }
}
