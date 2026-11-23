using ControlSystem.Contracts;
using ControlSystem.Server.Data;
using ControlSystem.Server.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace ControlSystem.Tests;

public sealed class MonitoringTests
{
    [Fact]
    public async Task Heartbeat_upserts_device_and_uses_server_clock_for_offline_boundary()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var service = new MonitoringService(new InMemoryDeviceStore(), clock, Options.Create(new MonitoringOptions()));
        var request = TestData.Heartbeat(clock.Now.AddMinutes(-1));
        var response = await service.ReceiveAsync(request, CancellationToken.None);
        Assert.Equal(clock.Now, response.ReceivedAtUtc);
        clock.Now = clock.Now.AddSeconds(35);
        Assert.Equal(DeviceStatus.Online, Assert.Single(await service.ListAsync(CancellationToken.None)).Status);
        clock.Now = clock.Now.AddMilliseconds(1);
        Assert.Equal(DeviceStatus.Offline, Assert.Single(await service.ListAsync(CancellationToken.None)).Status);
        await service.ReceiveAsync(request with { Hostname = "renamed" }, CancellationToken.None);
        var device = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.Equal(DeviceStatus.Online, device.Status);
        Assert.Equal("renamed", device.Hostname);
    }

    [Fact]
    public async Task Concurrent_updates_do_not_duplicate_or_roll_back_server_receipt_time()
    {
        var store = new InMemoryDeviceStore();
        var now = DateTimeOffset.UtcNow;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(n => Task.Run(() => store.UpsertAsync(
            DeviceSnapshot.From(TestData.Heartbeat(now) with { Hostname = n.ToString() }, now.AddSeconds(n)), CancellationToken.None))));
        var device = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(now.AddSeconds(99), device.LastSeenAtUtc);
        Assert.Equal("99", device.Hostname);
    }

    [Fact]
    public void Postgres_model_matches_migration_and_generates_atomic_schema_without_database()
    {
        using var db = new MonitoringDbContext(new DbContextOptionsBuilder<MonitoringDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE TABLE \"Devices\"", sql);
        Assert.Contains("PRIMARY KEY", sql);
        Assert.Contains("IX_Devices_LastSeenAtUtc", sql);
    }
}
