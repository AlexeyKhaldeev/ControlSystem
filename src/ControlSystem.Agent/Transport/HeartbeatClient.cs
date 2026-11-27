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
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/heartbeats")
        {
            Content = JsonContent.Create(heartbeat, options: MonitoringProtocol.Json)
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
