using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using ControlSystem.Agent;
using ControlSystem.Agent.Transport;
using ControlSystem.Contracts;
using ControlSystem.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServerProgram = ControlSystem.Server.Program;

namespace ControlSystem.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task Https_api_enforces_identity_validation_and_operator_access()
    {
        await using var server = await TestServer.StartAsync();
        using var client = server.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health/live", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health/ready", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/v1/devices", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("", TestContext.Current.CancellationToken)).StatusCode);
        // ASP.NET Core 10 treats SignalR as an API endpoint: cookie challenges return 401, not a login redirect.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("_blazor/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("api/v1/heartbeats", TestData.Heartbeat(), TestContext.Current.CancellationToken)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestServer.AgentToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("api/v1/devices", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("api/v1/heartbeats", TestData.Heartbeat() with { DeviceId = Guid.NewGuid() }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("api/v1/heartbeats", TestData.Heartbeat() with { Telemetry = null! }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("api/v1/heartbeats", TestData.Heartbeat(), TestContext.Current.CancellationToken)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestServer.OperatorToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("api/v1/heartbeats", TestData.Heartbeat(), TestContext.Current.CancellationToken)).StatusCode);
        var devices = await client.GetFromJsonAsync<DeviceResponse[]>("api/v1/devices", MonitoringProtocol.Json, TestContext.Current.CancellationToken);
        var device = Assert.Single(devices!);
        Assert.Equal(TestData.DeviceId, device.DeviceId);
        Assert.Equal(DeviceStatus.Online, device.Status);
        Assert.True(device.IsSimulated);
    }

    [Fact]
    public async Task Login_requires_antiforgery_and_renders_protected_dashboard()
    {
        await using var server = await TestServer.StartAsync();
        using var client = server.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = TestServer.OperatorToken }), TestContext.Current.CancellationToken)).StatusCode);
        var html = await client.GetStringAsync("login", TestContext.Current.CancellationToken);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        var login = await client.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = TestServer.OperatorToken, ["__RequestVerificationToken"] = token
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), value => value.Contains("secure", StringComparison.OrdinalIgnoreCase) && value.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Компьютеры экспозиции", await client.GetStringAsync("", TestContext.Current.CancellationToken));
        using var negotiation = await client.PostAsync("_blazor/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, negotiation.StatusCode);
        Assert.Equal("application/json", negotiation.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/v1/devices", TestContext.Current.CancellationToken)).StatusCode); // cookies cannot call bearer-only API
    }

    [Fact]
    public async Task Configured_ca_allows_agent_transport_and_wrong_ca_is_rejected()
    {
        await using var server = await TestServer.StartAsync();
        using var valid = server.Client();
        valid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestServer.AgentToken);
        await new HeartbeatClient(valid).SendAsync(TestData.Heartbeat(), CancellationToken.None);
        using var untrusted = AgentHttpClient.Create(new() { ServerUrl = server.Url, Token = TestServer.AgentToken, DeviceId = TestData.DeviceId });
        await Assert.ThrowsAsync<HttpRequestException>(() => untrusted.GetAsync("health/live", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Production_rejects_volatile_store() =>
        Assert.Throws<InvalidOperationException>(() => ServerProgram.CreateApp(["--environment", "Production"], b => b.Configuration["Database:Provider"] = "InMemory"));

    private sealed class TestServer : IAsyncDisposable
    {
        public const string AgentToken = "test-only-agent-token-000000000000000000000000";
        public const string OperatorToken = "test-only-operator-token-111111111111111111111";
        private readonly WebApplication _app;
        private readonly X509Certificate2 _certificate;
        private readonly string _caPath;
        public string Url { get; }

        private TestServer(WebApplication app, X509Certificate2 certificate, string caPath, string url) =>
            (_app, _certificate, _caPath, Url) = (app, certificate, caPath, url);

        public static async Task<TestServer> StartAsync()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var caPath = Path.Combine(Path.GetTempPath(), "controlsystem-test-" + Guid.NewGuid() + ".pem");
            await File.WriteAllTextAsync(caPath, certificate.ExportCertificatePem());
            var root = Path.GetFullPath("../../../../../src/ControlSystem.Server", AppContext.BaseDirectory);
            var app = ServerProgram.CreateApp(["--environment", "Testing", "--contentRoot", root], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "InMemory", ["AllowedHosts"] = "localhost;127.0.0.1",
                    ["Security:OperatorTokenHash"] = TokenDigest.Hash(OperatorToken),
                    ["Security:Agents:0:DeviceId"] = TestData.DeviceId.ToString(),
                    ["Security:Agents:0:TokenHash"] = TokenDigest.Hash(AgentToken)
                });
                builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
                builder.Logging.SetMinimumLevel(LogLevel.Error);
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            });
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, certificate, caPath, url.TrimEnd('/') + "/");
        }

        public HttpClient Client()
        {
            var client = AgentHttpClient.Create(new AgentOptions { ServerUrl = Url, DeviceId = TestData.DeviceId, Token = AgentToken, TrustedCaPath = _caPath });
            client.DefaultRequestHeaders.Authorization = null;
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _certificate.Dispose();
            File.Delete(_caPath);
        }
    }
}
