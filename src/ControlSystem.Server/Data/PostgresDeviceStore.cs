using ControlSystem.Server.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace ControlSystem.Server.Data;

public sealed class PostgresDeviceStore(IDbContextFactory<MonitoringDbContext> factory) : IDeviceStore
{
    public async Task UpsertAsync(DeviceSnapshot s, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // One atomic statement; no read/insert race and no interpolation into SQL text.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TelemetryHistory" ("DeviceId", "Hostname", "OperatingSystem", "AgentVersion", "SampledAtUtc", "LastSeenAtUtc", "IsSimulated", "TelemetryJson")
            VALUES ({s.DeviceId}, {s.Hostname}, {s.OperatingSystem}, {s.AgentVersion}, {s.SampledAtUtc}, {s.LastSeenAtUtc}, {s.IsSimulated}, {s.TelemetryJson})
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Devices" ("DeviceId", "Hostname", "OperatingSystem", "AgentVersion", "SampledAtUtc", "LastSeenAtUtc", "IsSimulated", "TelemetryJson")
            VALUES ({s.DeviceId}, {s.Hostname}, {s.OperatingSystem}, {s.AgentVersion}, {s.SampledAtUtc}, {s.LastSeenAtUtc}, {s.IsSimulated}, {s.TelemetryJson})
            ON CONFLICT ("DeviceId") DO UPDATE SET
              "Hostname" = EXCLUDED."Hostname", "OperatingSystem" = EXCLUDED."OperatingSystem",
              "AgentVersion" = EXCLUDED."AgentVersion", "SampledAtUtc" = EXCLUDED."SampledAtUtc",
              "LastSeenAtUtc" = EXCLUDED."LastSeenAtUtc", "IsSimulated" = EXCLUDED."IsSimulated",
              "TelemetryJson" = EXCLUDED."TelemetryJson"
            WHERE EXCLUDED."LastSeenAtUtc" >= "Devices"."LastSeenAtUtc"
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> PruneHistoryAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "TelemetryHistory" WHERE "Id" IN (
                SELECT "Id" FROM "TelemetryHistory" WHERE "LastSeenAtUtc" < {cutoff}
                ORDER BY "LastSeenAtUtc", "Id" LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED)
            """, cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Devices.AsNoTracking().OrderBy(d => d.Hostname).ToArrayAsync(cancellationToken);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // A table query checks schema availability as well as connectivity.
        _ = await db.Devices.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
        _ = await db.TelemetryHistory.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
        return true;
    }
}
