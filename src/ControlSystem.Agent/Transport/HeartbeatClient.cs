using System.Net.Http.Json;
using ControlSystem.Contracts;

namespace ControlSystem.Agent.Transport;

public interface IHeartbeatClient
{
    Task SendAsync(HeartbeatRequest heartbeat, CancellationToken cancellationToken);
}

public sealed class HeartbeatClient(HttpClient client) : IHeartbeatClient
{
    public async Task SendAsync(HeartbeatRequest heartbeat, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("api/v1/heartbeats", heartbeat, MonitoringProtocol.Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
