using ControlSystem.Contracts;

namespace ControlSystem.Tests;

internal static class TestData
{
    public static readonly Guid DeviceId = Guid.Parse("7765a61e-4ed5-4a86-a4ce-b75e88eb1e31");
    public static HeartbeatRequest Heartbeat(DateTimeOffset? now = null) => new(
        1, DeviceId, "EXHIBIT-01", "Windows 11", "0.1.0", now ?? DateTimeOffset.UtcNow, true,
        new(25, 16_000_000_000, 8_000_000_000, 100, [new("C:\\", 500_000_000_000, 200_000_000_000)]));
}

internal sealed class ManualClock(DateTimeOffset initial) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = initial;
    public override DateTimeOffset GetUtcNow() => Now;
}
