namespace ControlSystem.Server.Monitoring;

public interface IDeviceStore
{
    Task UpsertAsync(DeviceSnapshot snapshot, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}
