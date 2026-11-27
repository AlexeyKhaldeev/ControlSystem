using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ControlSystem.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using ControlSystem.Server.Data;
using ControlSystem.Server.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ControlSystem.Tests;

public sealed class PostgresHistoryTests
{
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTROLSYSTEM_TEST_POSTGRES"));
    private const string Prerequisite = "Set CONTROLSYSTEM_TEST_POSTGRES to a dedicated real PostgreSQL test database; no InMemory fallback.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(SkipUnless = nameof(IsConfigured), Skip = Prerequisite)]
    public async Task Migration_preserves_existing_latest_snapshot_and_is_repeatable()
    {
        await using var database = await TestDatabase.CreateAsync(migrate: false);
        await using var db = database.Context();
        // Legacy migration IDs contain 12 digits; EF resolves their generated name rather than treating them as valid 14-digit IDs.
        var initialName = db.GetService<IMigrationsIdGenerator>().GetName("202611220001_InitialMonitoring");
        await db.GetService<IMigrator>().MigrateAsync(initialName, Ct);
        var original = Snapshot(0);
        db.Devices.Add(original);
        await db.SaveChangesAsync(Ct);
        await db.Database.MigrateAsync(Ct);
        await db.Database.MigrateAsync(Ct);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(original.TelemetryJson, (await db.Devices.SingleAsync(Ct)).TelemetryJson);
        // Migration does not invent historical receipts for pre-upgrade snapshots.
        Assert.Empty(await db.TelemetryHistory.ToArrayAsync(Ct));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = @schema AND tablename = 'TelemetryHistory'", connection);
        command.Parameters.AddWithValue("schema", database.Schema);
        var definitions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) definitions.Add(reader.GetString(0));
        Assert.Contains(definitions, value => value.Contains("(\"DeviceId\", \"LastSeenAtUtc\", \"Id\")"));
        Assert.Contains(definitions, value => value.Contains("(\"LastSeenAtUtc\", \"Id\")"));
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = Prerequisite)]
    public async Task Concurrent_receipts_append_every_snapshot_without_rolling_back_latest()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var services = database.Services();
        var store = services.GetRequiredService<PostgresDeviceStore>();
        await Task.WhenAll(Enumerable.Range(0, 40).Reverse().Select(n => store.UpsertAsync(Snapshot(n), Ct)));
        await using var db = database.Context();
        Assert.Equal(40, await db.TelemetryHistory.CountAsync(Ct));
        var latest = Assert.Single(await store.ListAsync(Ct));
        Assert.Equal(Snapshot(39).LastSeenAtUtc, latest.LastSeenAtUtc);
        Assert.Equal("receipt-39", latest.Hostname);
        var history = await db.TelemetryHistory.OrderBy(h => h.LastSeenAtUtc).ToArrayAsync(Ct);
        Assert.Equal(Enumerable.Range(0, 40).Select(n => Snapshot(n).TelemetryJson), history.Select(h => h.TelemetryJson));
        Assert.Equal(40, history.Select(h => h.Id).Distinct().Count());
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = Prerequisite)]
    public async Task History_failure_rolls_back_latest_in_same_transaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var services = database.Services();
        var store = services.GetRequiredService<PostgresDeviceStore>();
        await store.UpsertAsync(Snapshot(0), Ct);
        await using var db = database.Context();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"TelemetryHistory\" ADD CONSTRAINT test_reject_history CHECK (\"Hostname\" <> 'receipt-1')", Ct);
        await Assert.ThrowsAsync<PostgresException>(() => store.UpsertAsync(Snapshot(1), Ct));
        Assert.Equal("receipt-0", Assert.Single(await store.ListAsync(Ct)).Hostname);
        Assert.Equal(1, await db.TelemetryHistory.CountAsync(Ct));
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = Prerequisite)]
    public async Task Latest_failure_rolls_back_history_insert_in_same_transaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var services = database.Services();
        var store = services.GetRequiredService<PostgresDeviceStore>();
        await store.UpsertAsync(Snapshot(0), Ct);
        await using var db = database.Context();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Devices\" ADD CONSTRAINT test_reject_latest CHECK (\"Hostname\" <> 'receipt-1')", Ct);
        await Assert.ThrowsAsync<PostgresException>(() => store.UpsertAsync(Snapshot(1), Ct));
        Assert.Equal("receipt-0", Assert.Single(await store.ListAsync(Ct)).Hostname);
        Assert.Equal(1, await db.TelemetryHistory.CountAsync(Ct));
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = Prerequisite)]
    public async Task Retention_is_bounded_excludes_cutoff_and_never_removes_latest()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var services = database.Services();
        var store = services.GetRequiredService<PostgresDeviceStore>();
        foreach (var n in Enumerable.Range(0, 5)) await store.UpsertAsync(Snapshot(n), Ct);
        var cutoff = Snapshot(3).LastSeenAtUtc;
        Assert.Equal(2, await store.PruneHistoryAsync(cutoff, 2, Ct));
        Assert.Equal(1, await store.PruneHistoryAsync(cutoff, 2, Ct));
        Assert.Equal(0, await store.PruneHistoryAsync(cutoff, 2, Ct));
        await using var db = database.Context();
        Assert.Equal(new[] { Snapshot(3).LastSeenAtUtc, Snapshot(4).LastSeenAtUtc },
            await db.TelemetryHistory.OrderBy(h => h.LastSeenAtUtc).Select(h => h.LastSeenAtUtc).ToArrayAsync(Ct));
        Assert.Equal(2, await store.PruneHistoryAsync(cutoff.AddDays(1), 100, Ct));
        Assert.Equal("receipt-4", Assert.Single(await store.ListAsync(Ct)).Hostname);
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = Prerequisite)]
    public async Task History_and_latest_survive_real_server_stop_dispose_and_restart()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        await using (var first = CreateServer(database.ConnectionString, certificate))
        {
            await first.StartAsync(Ct);
            await first.Services.GetRequiredService<IDeviceStore>().UpsertAsync(Snapshot(0), Ct);
            await first.StopAsync(Ct);
        }
        NpgsqlConnection.ClearAllPools();
        await using var restarted = CreateServer(database.ConnectionString, certificate);
        await restarted.StartAsync(Ct);
        try
        {
            var store = restarted.Services.GetRequiredService<IDeviceStore>();
            Assert.True(await store.IsReadyAsync(Ct));
            Assert.Equal(Snapshot(0).TelemetryJson, Assert.Single(await store.ListAsync(Ct)).TelemetryJson);
            var factory = restarted.Services.GetRequiredService<IDbContextFactory<MonitoringDbContext>>();
            await using var db = await factory.CreateDbContextAsync(Ct);
            Assert.Equal(Snapshot(0).TelemetryJson, (await db.TelemetryHistory.SingleAsync(Ct)).TelemetryJson);
        }
        finally { await restarted.StopAsync(Ct); }
    }

    private static WebApplication CreateServer(string connectionString, X509Certificate2 certificate) =>
        ControlSystem.Server.Program.CreateApp(["--environment", "Testing", "--contentRoot",
            Path.GetFullPath("../../../../../src/ControlSystem.Server", AppContext.BaseDirectory)], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "PostgreSql", ["ConnectionStrings:Monitoring"] = connectionString,
                ["HistoryRetention:Enabled"] = "false", ["AllowedHosts"] = "localhost;127.0.0.1",
                ["Security:OperatorTokenHash"] = TokenDigest.Hash("history-test-operator-token-000000000000000"),
                ["Security:Agents:0:DeviceId"] = TestData.DeviceId.ToString(),
                ["Security:Agents:0:TokenHash"] = TokenDigest.Hash("history-test-agent-token-111111111111111111")
            });
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        });

    [Fact]
    public void Retention_configuration_defaults_to_disabled_and_rejects_unbounded_values()
    {
        Assert.False(new HistoryRetentionOptions().Enabled);
        Assert.True(new HistoryRetentionOptions().IsValid());
        Assert.False(new HistoryRetentionOptions { RetentionDays = 0 }.IsValid());
        Assert.False(new HistoryRetentionOptions { RetentionDays = 3651 }.IsValid());
        Assert.False(new HistoryRetentionOptions { BatchSize = 0 }.IsValid());
        Assert.False(new HistoryRetentionOptions { BatchSize = 10001 }.IsValid());
        Assert.False(new HistoryRetentionOptions { IntervalMinutes = 0 }.IsValid());
        Assert.False(new HistoryRetentionOptions { IntervalMinutes = 1441 }.IsValid());
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = Prerequisite)]
    public async Task Configured_worker_is_opt_in_and_runs_one_bounded_batch_with_server_time()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var services = database.Services();
        var store = services.GetRequiredService<PostgresDeviceStore>();
        foreach (var n in Enumerable.Range(0, 5)) await store.UpsertAsync(Snapshot(n), Ct);
        var clock = new ManualClock(Snapshot(3).LastSeenAtUtc.AddDays(30));
        using (var disabled = new HistoryRetentionService(store, Options.Create(new HistoryRetentionOptions()), clock,
                   NullLogger<HistoryRetentionService>.Instance))
        {
            await disabled.StartAsync(Ct);
            await disabled.StopAsync(Ct);
        }
        await using var db = database.Context();
        Assert.Equal(5, await db.TelemetryHistory.CountAsync(Ct));
        using var enabled = new HistoryRetentionService(store, Options.Create(new HistoryRetentionOptions
        {
            Enabled = true, RetentionDays = 30, BatchSize = 2, IntervalMinutes = 60
        }), clock, NullLogger<HistoryRetentionService>.Instance);
        await enabled.StartAsync(Ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (await db.TelemetryHistory.CountAsync(deadline.Token) == 5)
                await Task.Delay(20, deadline.Token);
            Assert.Equal(3, await db.TelemetryHistory.CountAsync(Ct));
            Assert.Equal(Snapshot(2).LastSeenAtUtc, await db.TelemetryHistory.MinAsync(h => h.LastSeenAtUtc, Ct));
            Assert.Equal("receipt-4", Assert.Single(await store.ListAsync(Ct)).Hostname);
        }
        finally { await enabled.StopAsync(Ct); }
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = Prerequisite)]
    public async Task Measure_postgres_history_write_and_device_range_query_smoke()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var services = database.Services();
        var store = services.GetRequiredService<PostgresDeviceStore>();
        var deviceIds = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        var latencies = new double[1000];
        var total = Stopwatch.StartNew();
        for (var n = 0; n < latencies.Length; n++)
        {
            var snapshot = Snapshot(n);
            snapshot.DeviceId = deviceIds[n % deviceIds.Length];
            var operation = Stopwatch.StartNew();
            await store.UpsertAsync(snapshot, Ct);
            latencies[n] = operation.Elapsed.TotalMilliseconds;
        }
        total.Stop();
        Array.Sort(latencies);
        await using var db = database.Context();
        Assert.Equal(1000, await db.TelemetryHistory.CountAsync(Ct));
        Assert.Equal(100, await db.Devices.CountAsync(Ct));
        var rangeStart = Snapshot(250).LastSeenAtUtc;
        var rangeEnd = Snapshot(750).LastSeenAtUtc;
        Assert.Equal(5, await db.TelemetryHistory.CountAsync(h => h.DeviceId == deviceIds[0] &&
            h.LastSeenAtUtc >= rangeStart && h.LastSeenAtUtc < rangeEnd, Ct));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using (var analyze = new NpgsqlCommand("ANALYZE \"TelemetryHistory\"", connection))
            await analyze.ExecuteNonQueryAsync(Ct);
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) SELECT * FROM \"TelemetryHistory\" WHERE \"DeviceId\" = @device AND \"LastSeenAtUtc\" >= @start AND \"LastSeenAtUtc\" < @end ORDER BY \"LastSeenAtUtc\", \"Id\"", connection);
        explain.Parameters.AddWithValue("device", deviceIds[0]);
        explain.Parameters.AddWithValue("start", Snapshot(250).LastSeenAtUtc);
        explain.Parameters.AddWithValue("end", Snapshot(750).LastSeenAtUtc);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct)) plan.Add(reader.GetString(0));
        await using var size = new NpgsqlCommand("SELECT pg_total_relation_size('\"TelemetryHistory\"'::regclass)", connection);
        var bytes = (long)(await size.ExecuteScalarAsync(Ct))!;
        TestContext.Current.TestOutputHelper!.WriteLine($"100 devices / 1000 sequential committed writes: elapsed={total.Elapsed.TotalMilliseconds:F1}ms, rate={1000 / total.Elapsed.TotalSeconds:F1}/s, p50={latencies[499]:F2}ms, p95={latencies[949]:F2}ms; history table+indexes={bytes} bytes. Local smoke only, not sustained load.");
        TestContext.Current.TestOutputHelper.WriteLine(string.Join(Environment.NewLine, plan));
    }

    private static DeviceSnapshot Snapshot(int n) => DeviceSnapshot.From(
        TestData.Heartbeat(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero).AddSeconds(n)) with { Hostname = $"receipt-{n}", Telemetry = TestData.Heartbeat().Telemetry with { CpuUsagePercent = n % 100 } },
        new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero).AddSeconds(n));

    private sealed class TestDatabase(string connectionString, string schema) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public string Schema { get; } = schema;
        public MonitoringDbContext Context() => new(new DbContextOptionsBuilder<MonitoringDbContext>().UseNpgsql(ConnectionString).Options);
        public ServiceProvider Services() => new ServiceCollection()
            .AddDbContextFactory<MonitoringDbContext>(options => options.UseNpgsql(ConnectionString))
            .AddSingleton<PostgresDeviceStore>().BuildServiceProvider();

        public static async Task<TestDatabase> CreateAsync(bool migrate = true)
        {
            var schema = "history_test_" + Guid.NewGuid().ToString("N");
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CONTROLSYSTEM_TEST_POSTGRES")) { SearchPath = schema };
            await using var admin = new NpgsqlConnection(connection.ConnectionString);
            await admin.OpenAsync(Ct);
            // Only a generated identifier enters DDL; no user-provided identifier or value is interpolated.
            await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
            await create.ExecuteNonQueryAsync(Ct);
            var database = new TestDatabase(connection.ConnectionString, schema);
            try
            {
                if (migrate)
                {
                    await using var db = database.Context();
                    await db.Database.MigrateAsync(Ct);
                }
                return database;
            }
            catch { await database.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{Schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
