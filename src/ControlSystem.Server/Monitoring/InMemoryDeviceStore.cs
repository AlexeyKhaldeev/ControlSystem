using System.Collections.Concurrent;

namespace ControlSystem.Server.Monitoring;

// Explicit volatile simulation store, never a fallback for a failing database.
public sealed class InMemoryDeviceStore : IDeviceStore
{
    private readonly ConcurrentDictionary<Guid, DeviceSnapshot> _devices = new();

    public Task UpsertAsync(DeviceSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _devices.AddOrUpdate(snapshot.DeviceId, snapshot, (_, current) =>
            snapshot.LastSeenAtUtc >= current.LastSeenAtUtc ? snapshot : current);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceSnapshot>>(_devices.Values.OrderBy(d => d.Hostname).ToArray());

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}
