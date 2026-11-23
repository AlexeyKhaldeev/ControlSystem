using ControlSystem.Contracts;

namespace ControlSystem.Agent.Telemetry;

public interface ITelemetryCollector
{
    bool IsSimulated { get; }
    string Hostname { get; }
    string OperatingSystemName { get; }
    Task<TelemetrySnapshot> CollectAsync(CancellationToken cancellationToken);
}
