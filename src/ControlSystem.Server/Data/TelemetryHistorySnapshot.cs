
namespace ControlSystem.Server.Data;

public sealed class TelemetryHistorySnapshot
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public string Hostname { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public DateTimeOffset SampledAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public bool IsSimulated { get; set; }
    public string TelemetryJson { get; set; } = "";

}
