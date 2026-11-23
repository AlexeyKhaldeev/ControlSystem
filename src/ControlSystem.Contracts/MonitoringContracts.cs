using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlSystem.Contracts;

public static class MonitoringProtocol
{
    public const int Version = 1;
    public const int HeartbeatIntervalSeconds = 10;
    public const int OfflineAfterSeconds = 35;
    public const int MaxDisks = 32;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<DeviceStatus>() }
    };
}

public enum DeviceStatus { Online, Offline }

public sealed record DiskTelemetry(string Name, long TotalBytes, long FreeBytes);

public sealed record TelemetrySnapshot(
    double? CpuUsagePercent,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    long UptimeSeconds,
    IReadOnlyList<DiskTelemetry> Disks);

public sealed record HeartbeatRequest(
    int ProtocolVersion,
    Guid DeviceId,
    string Hostname,
    string OperatingSystem,
    string AgentVersion,
    DateTimeOffset SampledAtUtc,
    bool IsSimulated,
    TelemetrySnapshot Telemetry);

public sealed record HeartbeatResponse(DateTimeOffset ReceivedAtUtc, int NextHeartbeatSeconds);

public sealed record DeviceResponse(
    Guid DeviceId,
    string Hostname,
    string OperatingSystem,
    string AgentVersion,
    DateTimeOffset SampledAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DeviceStatus Status,
    bool IsSimulated,
    TelemetrySnapshot Telemetry);
