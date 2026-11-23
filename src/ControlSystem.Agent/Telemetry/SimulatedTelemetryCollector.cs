using System.Diagnostics;
using ControlSystem.Contracts;

namespace ControlSystem.Agent.Telemetry;

public sealed class SimulatedTelemetryCollector : ITelemetryCollector
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    public bool IsSimulated => true;
    public string Hostname => "SIM-" + Environment.MachineName;
    public string OperatingSystemName => "Windows 11 (simulated on " + System.Runtime.InteropServices.RuntimeInformation.OSDescription + ")";

    public Task<TelemetrySnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const long gib = 1_073_741_824;
        return Task.FromResult(new TelemetrySnapshot(
            Math.Round(15 + Random.Shared.NextDouble() * 55, 1),
            16 * gib, (long)((4 + Random.Shared.NextDouble() * 6) * gib),
            (long)_uptime.Elapsed.TotalSeconds,
            [new("C:\\", 512 * gib, 210 * gib), new("D:\\", 1024 * gib, 680 * gib)]));
    }
}
