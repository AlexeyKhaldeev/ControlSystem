using Microsoft.Extensions.Options;

namespace ControlSystem.Server.Data;

public sealed class HistoryRetentionOptions
{
    public bool Enabled { get; set; }
    public int RetentionDays { get; set; } = 30;
    public int BatchSize { get; set; } = 10000;
    public int IntervalMinutes { get; set; } = 1;
    public bool IsValid() => RetentionDays is >= 1 and <= 3650
        && BatchSize is >= 1 and <= 10000 && IntervalMinutes is >= 1 and <= 1440;
}

public sealed class HistoryRetentionService(
    PostgresDeviceStore store, IOptions<HistoryRetentionOptions> options,
    TimeProvider clock, ILogger<HistoryRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policy = options.Value;
        if (!policy.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // One bounded batch per tick prevents a backlog from monopolizing the database.
                var removed = await store.PruneHistoryAsync(
                    clock.GetUtcNow().AddDays(-policy.RetentionDays), policy.BatchSize, stoppingToken);
                logger.LogInformation("History retention removed {Count} snapshots", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(error, "History retention failed; retrying next interval"); }
            await Task.Delay(TimeSpan.FromMinutes(policy.IntervalMinutes), clock, stoppingToken);
        }
    }
}
