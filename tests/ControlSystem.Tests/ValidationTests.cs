using ControlSystem.Contracts;

namespace ControlSystem.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void Valid_heartbeat_including_warmup_cpu_is_accepted()
    {
        var request = TestData.Heartbeat();
        Assert.Empty(HeartbeatValidator.Validate(request, request.SampledAtUtc));
        Assert.Empty(HeartbeatValidator.Validate(request with { Telemetry = request.Telemetry with { CpuUsagePercent = null } }, request.SampledAtUtc));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_cpu_is_rejected(double cpu)
    {
        var request = TestData.Heartbeat();
        Assert.Contains("telemetry.cpuUsagePercent", HeartbeatValidator.Validate(request with { Telemetry = request.Telemetry with { CpuUsagePercent = cpu } }, request.SampledAtUtc).Keys);
    }

    [Fact]
    public void Missing_nested_data_and_impossible_resources_are_rejected()
    {
        var r = TestData.Heartbeat();
        HeartbeatRequest[] invalid = [
            r with { Telemetry = null! },
            r with { Telemetry = r.Telemetry with { Disks = null! } },
            r with { Telemetry = r.Telemetry with { Disks = [null!] } },
            r with { Telemetry = r.Telemetry with { MemoryAvailableBytes = r.Telemetry.MemoryTotalBytes + 1 } },
            r with { Telemetry = r.Telemetry with { MemoryTotalBytes = 0 } },
            r with { Telemetry = r.Telemetry with { UptimeSeconds = long.MaxValue } },
            r with { Telemetry = r.Telemetry with { Disks = [new("C:", 10, 11)] } },
            r with { Telemetry = r.Telemetry with { Disks = [new("C:", 10, 1), new("c:", 10, 1)] } },
            r with { Telemetry = r.Telemetry with { Disks = Enumerable.Range(0, 33).Select(n => new DiskTelemetry(n.ToString(), 10, 1)).ToArray() } },
            r with { Hostname = null! }, r with { DeviceId = Guid.Empty }, r with { ProtocolVersion = 99 },
            r with { SampledAtUtc = r.SampledAtUtc.AddMinutes(-6) }
        ];
        foreach (var request in invalid) Assert.NotEmpty(HeartbeatValidator.Validate(request, r.SampledAtUtc));
    }
}
