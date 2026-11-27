using System.ComponentModel;
using System.Reflection;
using ControlSystem.Agent.Telemetry;
using ControlSystem.Agent.Transport;
using ControlSystem.Contracts;
using Microsoft.Extensions.Options;

namespace ControlSystem.Agent;

public sealed class TelemetryWorker(
    ITelemetryCollector collector,
    IHeartbeatClient client,
    IOptions<AgentOptions> options,
    TimeProvider clock,
    ILogger<TelemetryWorker> logger,
    Action<int>? fatalExit = null) : BackgroundService
{
    public static string AgentVersion { get; } =
        typeof(TelemetryWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(TelemetryWorker).Assembly.GetName().Version!.ToString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (Exception error)
        {
            logger.LogCritical("Agent terminated: {ErrorType}", error.GetType().Name);
            (fatalExit ?? Environment.Exit)(1);
            throw new InvalidOperationException("Agent terminated after an unexpected worker failure.");
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        logger.LogInformation("Agent {DeviceId} started. Simulated: {Simulated}", options.Value.DeviceId, collector.IsSimulated);
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = clock.GetTimestamp();
            try
            {
                var telemetry = await collector.CollectAsync(stoppingToken);
                var heartbeat = new HeartbeatRequest(MonitoringProtocol.Version, options.Value.DeviceId, collector.Hostname,
                    collector.OperatingSystemName, AgentVersion, clock.GetUtcNow(), collector.IsSimulated, telemetry);
                if (HeartbeatValidator.Validate(heartbeat, clock.GetUtcNow()).Count != 0)
                    throw new IOException("Collected telemetry does not satisfy the heartbeat contract.");
                await client.SendAsync(heartbeat, stoppingToken);
                failures = 0;
                logger.LogInformation("Heartbeat accepted for {DeviceId}", heartbeat.DeviceId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or Win32Exception or IOException or UnauthorizedAccessException)
            {
                failures = Math.Min(failures + 1, 4);
                logger.LogWarning("Heartbeat failed: {ErrorType}. HTTP status: {Status}", error.GetType().Name, (error as HttpRequestException)?.StatusCode);
            }
            var interval = failures == 0 ? 10 : Math.Min(60, 10 * (1 << (failures - 1))) + Random.Shared.NextDouble() * 2;
            var delay = TimeSpan.FromSeconds(interval) - (failures == 0 ? clock.GetElapsedTime(started) : TimeSpan.Zero);
            try { if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
