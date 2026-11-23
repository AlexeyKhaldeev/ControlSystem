using System.Net;
using ControlSystem.Agent;
using ControlSystem.Agent.Telemetry;
using ControlSystem.Agent.Transport;
using ControlSystem.Contracts;
using ControlSystem.Server.Security;

namespace ControlSystem.Tests;

public sealed class SecurityAndAgentTests
{
    [Fact]
    public void Tokens_are_hashed_and_config_requires_distinct_credentials()
    {
        const string token = "a-real-random-token-would-be-generated-by-setup";
        var hash = TokenDigest.Hash(token);
        Assert.True(TokenDigest.Matches(token, hash));
        Assert.False(TokenDigest.Matches("wrong", hash));
        Assert.False(TokenDigest.Matches(token, "invalid"));
        var options = new SecurityOptions { OperatorTokenHash = hash, Agents = [new() { DeviceId = TestData.DeviceId, TokenHash = hash }] };
        Assert.False(options.IsValid());
        options.Agents[0].TokenHash = TokenDigest.Hash(token + "agent");
        Assert.True(options.IsValid());
        options.Agents.Add(options.Agents[0]);
        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("https://user:pass@localhost/")]
    [InlineData("https://localhost/wrong-path/")]
    public void Agent_rejects_insecure_or_ambiguous_server_urls(string url) =>
        Assert.False(new AgentOptions { ServerUrl = url, DeviceId = TestData.DeviceId, Token = new('a', 43) }.IsValid());

    [Fact]
    public async Task Simulation_is_explicit_and_produces_valid_full_telemetry()
    {
        var collector = new SimulatedTelemetryCollector();
        var telemetry = await collector.CollectAsync(CancellationToken.None);
        var request = TestData.Heartbeat() with { Telemetry = telemetry };
        Assert.True(collector.IsSimulated);
        Assert.StartsWith("SIM-", collector.Hostname);
        Assert.Empty(HeartbeatValidator.Validate(request, request.SampledAtUtc));
        Assert.Equal(2, telemetry.Disks.Count);
    }

    [Fact]
    public async Task Heartbeat_transport_propagates_unauthorized_response()
    {
        using var http = new HttpClient(new RejectingHandler()) { BaseAddress = new("https://localhost/") };
        var client = new HeartbeatClient(http);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(TestData.Heartbeat(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/heartbeats", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}
