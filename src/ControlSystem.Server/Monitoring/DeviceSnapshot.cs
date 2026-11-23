using System.Text.Json;
using ControlSystem.Contracts;

namespace ControlSystem.Server.Monitoring;

public sealed class DeviceSnapshot
{
    public Guid DeviceId { get; set; }
    public string Hostname { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public DateTimeOffset SampledAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public bool IsSimulated { get; set; }
    public string TelemetryJson { get; set; } = "";

    public static DeviceSnapshot From(HeartbeatRequest request, DateTimeOffset receivedAt) => new()
    {
        DeviceId = request.DeviceId,
        Hostname = request.Hostname,
        OperatingSystem = request.OperatingSystem,
        AgentVersion = request.AgentVersion,
        SampledAtUtc = request.SampledAtUtc.ToUniversalTime(),
        LastSeenAtUtc = receivedAt.ToUniversalTime(),
        IsSimulated = request.IsSimulated,
        TelemetryJson = JsonSerializer.Serialize(request.Telemetry, MonitoringProtocol.Json)
    };

    public DeviceResponse ToResponse(DateTimeOffset now, TimeSpan offlineAfter) => new(
        DeviceId, Hostname, OperatingSystem, AgentVersion, SampledAtUtc, LastSeenAtUtc,
        now - LastSeenAtUtc > offlineAfter ? DeviceStatus.Offline : DeviceStatus.Online,
        IsSimulated, JsonSerializer.Deserialize<TelemetrySnapshot>(TelemetryJson, MonitoringProtocol.Json)!);
}
