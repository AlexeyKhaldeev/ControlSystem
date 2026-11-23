namespace ControlSystem.Contracts;

public static class HeartbeatValidator
{
    public static Dictionary<string, string[]> Validate(HeartbeatRequest request, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool condition, string field, string message)
        {
            if (!condition) errors[field] = [message];
        }

        Check(request.ProtocolVersion == MonitoringProtocol.Version, "protocolVersion", "Unsupported protocol version.");
        Check(request.DeviceId != Guid.Empty, "deviceId", "A stable non-empty device ID is required.");
        Check(!string.IsNullOrWhiteSpace(request.Hostname) && request.Hostname.Length <= 120, "hostname", "Expected 1–120 characters.");
        Check(!string.IsNullOrWhiteSpace(request.OperatingSystem) && request.OperatingSystem.Length <= 200, "operatingSystem", "Expected 1–200 characters.");
        Check(!string.IsNullOrWhiteSpace(request.AgentVersion) && request.AgentVersion.Length <= 40, "agentVersion", "Expected 1–40 characters.");
        Check((now - request.SampledAtUtc).Duration() <= TimeSpan.FromMinutes(5), "sampledAtUtc", "Clock skew must not exceed 5 minutes; synchronize NTP.");
        if (request.Telemetry is not { } t)
        {
            errors["telemetry"] = ["Telemetry is required."];
            return errors;
        }

        Check(t.CpuUsagePercent is null || (double.IsFinite(t.CpuUsagePercent.Value) && t.CpuUsagePercent is >= 0 and <= 100), "telemetry.cpuUsagePercent", "Expected null or a finite percentage from 0 to 100.");
        Check(t.MemoryTotalBytes > 0, "telemetry.memoryTotalBytes", "Must be positive.");
        Check(t.MemoryAvailableBytes >= 0 && t.MemoryAvailableBytes <= t.MemoryTotalBytes, "telemetry.memoryAvailableBytes", "Must be between 0 and total memory.");
        Check(t.UptimeSeconds is >= 0 and <= 3_155_760_000, "telemetry.uptimeSeconds", "Must be between zero and 100 years.");
        if (t.Disks is null || t.Disks.Count > MonitoringProtocol.MaxDisks)
        {
            errors["telemetry.disks"] = ["Expected an array of at most 32 disks."];
            return errors;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var disk in t.Disks)
        {
            if (disk is null || string.IsNullOrWhiteSpace(disk.Name) || disk.Name.Length > 200 ||
                !names.Add(disk.Name) || disk.TotalBytes <= 0 || disk.FreeBytes < 0 || disk.FreeBytes > disk.TotalBytes)
            {
                errors["telemetry.disks"] = ["Disk names must be unique, 1–200 characters; 0 <= freeBytes <= totalBytes and totalBytes > 0."];
                break;
            }
        }
        return errors;
    }
}
