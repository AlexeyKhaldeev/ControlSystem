using ControlSystem.Contracts;
using Microsoft.Extensions.Options;

namespace ControlSystem.Server.Monitoring;

public sealed class MonitoringOptions
{
    public int OfflineAfterSeconds { get; set; } = MonitoringProtocol.OfflineAfterSeconds;
}

public sealed class MonitoringService(IDeviceStore store, TimeProvider clock, IOptions<MonitoringOptions> options)
{
    public async Task<HeartbeatResponse> ReceiveAsync(HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var receivedAt = clock.GetUtcNow();
        await store.UpsertAsync(DeviceSnapshot.From(request, receivedAt), cancellationToken);
        return new(receivedAt, MonitoringProtocol.HeartbeatIntervalSeconds);
    }

    public async Task<IReadOnlyList<DeviceResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var devices = await store.ListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return devices.Select(d => d.ToResponse(now, TimeSpan.FromSeconds(options.Value.OfflineAfterSeconds))).ToArray();
    }
}
