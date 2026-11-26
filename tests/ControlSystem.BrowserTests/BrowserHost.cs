using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ControlSystem.Contracts;
using ControlSystem.Server.Monitoring;
using ControlSystem.Server.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using ServerProgram = ControlSystem.Server.Program;

namespace ControlSystem.BrowserTests;

// Isolated, opt-in real-browser harness. No production endpoints/options are changed.
internal sealed class BrowserHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly SessionClock _clock;
    private readonly Guid _deviceId = Guid.NewGuid();
    public string Url { get; }
    public string OperatorToken { get; }
    public string AgentToken { get; }
    public CountingStore Store { get; }
    public ConnectionProbe Probe { get; }

    private BrowserHost(WebApplication app, X509Certificate2 certificate, IPlaywright playwright,
        IBrowser browser, SessionClock clock, string url, string operatorToken, string agentToken,
        CountingStore store, ConnectionProbe probe)
    {
        (_app, _certificate, _playwright, _browser, _clock, Url, OperatorToken, AgentToken, Store, Probe) =
            (app, certificate, playwright, browser, clock, url, operatorToken, agentToken, store, probe);
    }

    public static async Task<BrowserHost> StartAsync(bool untrustedTls = false)
    {
        var certificatePath = Environment.GetEnvironmentVariable("CONTROLSYSTEM_E2E_CERT");
        var keyPath = Environment.GetEnvironmentVariable("CONTROLSYSTEM_E2E_KEY");
        if (string.IsNullOrEmpty(certificatePath) || string.IsNullOrEmpty(keyPath))
            throw new InvalidOperationException("Set CONTROLSYSTEM_E2E_CERT and CONTROLSYSTEM_E2E_KEY to a browser-trusted localhost PEM certificate/key. See docs/browser-e2e.md. TLS bypass is prohibited.");
        var certificate = untrustedTls ? CreateUntrustedCertificate() : X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        var clock = new SessionClock();
        var store = new CountingStore();
        var probe = new ConnectionProbe();
        var operatorToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var agentToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var root = Path.GetFullPath("../../../../../src/ControlSystem.Server", AppContext.BaseDirectory);
        var app = ServerProgram.CreateApp(["--environment", "Testing", "--contentRoot", root], builder =>
        {
            // Use real built Blazor assets even though this harness has a different entry assembly.
            builder.WebHost.UseStaticWebAssets();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "InMemory", ["AllowedHosts"] = "localhost;127.0.0.1",
                ["Security:OperatorTokenHash"] = TokenDigest.Hash(operatorToken),
                ["Security:Agents:0:DeviceId"] = Guid.NewGuid().ToString(),
                ["Security:Agents:0:TokenHash"] = TokenDigest.Hash(agentToken)
            });
            // Program's configure callback precedes its own registrations. Defer overrides
            // to container build, otherwise the production registrations would win.
            builder.Host.UseServiceProviderFactory(new OverrideServiceProviderFactory(clock, store));
            builder.Services.AddSingleton<CircuitHandler>(probe);
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme,
                options => options.TimeProvider = clock);
            builder.Logging.SetMinimumLevel(LogLevel.Error);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        });
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            if (!ReferenceEquals(app.Services.GetRequiredService<IDeviceStore>(), store)
                || !ReferenceEquals(app.Services.GetRequiredService<TimeProvider>(), clock))
                throw new InvalidOperationException("E2E container overrides were not applied.");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var url = address.Replace("127.0.0.1", "localhost").TrimEnd('/') + "/";
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new()
            {
                Channel = "chrome", Headless = true,
                // Neither certificate-error flags nor IgnoreHTTPSErrors are used.
            });
            return new(app, certificate, playwright, browser, clock, url, operatorToken, agentToken, store, probe);
        }
        catch
        {
            if (browser is not null) await browser.DisposeAsync();
            playwright?.Dispose();
            await app.DisposeAsync();
            certificate.Dispose();
            throw;
        }
    }

    public Task<IBrowserContext> CreateContextAsync() => _browser.NewContextAsync(new()
    {
        BaseURL = Url, IgnoreHTTPSErrors = false, ServiceWorkers = ServiceWorkerPolicy.Block
    });

    public async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(Url + "login");
        await page.GetByLabel("Ключ доступа").FillAsync(OperatorToken);
        await page.GetByRole(AriaRole.Button, new() { Name = "Открыть мониторинг" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Компьютеры экспозиции" })).ToBeVisibleAsync();
        await using var script = await page.APIRequest.GetAsync(Url + "_framework/blazor.web.js");
        if (script.Status != 200) throw new InvalidOperationException($"Blazor bootstrap script returned {script.Status}.");
        await page.WaitForFunctionAsync("typeof Blazor !== 'undefined'", null, new() { Timeout = 10_000 });
    }

    public Task<IPage> NewPageAsync(IBrowserContext context) => context.NewPageAsync();
    public void ExpireSession() => _clock.Advance(TimeSpan.FromHours(9));

    public Task PublishAsync(string marker) => Store.UpsertAsync(DeviceSnapshot.From(new HeartbeatRequest(
        1, _deviceId, marker, "E2E simulated OS", "0.2-e2e", _clock.GetUtcNow(), true,
        new(25, 16_000_000_000, 8_000_000_000, 100, [])), _clock.GetUtcNow()), CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _certificate.Dispose();
    }

    private static X509Certificate2 CreateUntrustedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class OverrideServiceProviderFactory(TimeProvider clock, IDeviceStore store)
        : IServiceProviderFactory<IServiceCollection>
    {
        private readonly DefaultServiceProviderFactory _inner = new(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        public IServiceCollection CreateBuilder(IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Singleton(clock));
            services.Replace(ServiceDescriptor.Singleton(store));
            return _inner.CreateBuilder(services);
        }
        public IServiceProvider CreateServiceProvider(IServiceCollection services) => _inner.CreateServiceProvider(services);
    }

    private sealed class SessionClock : TimeProvider
    {
        private long _offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offset));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _offset, duration.Ticks);
    }
}

internal sealed class CountingStore : IDeviceStore
{
    private readonly InMemoryDeviceStore _inner = new();
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public Task UpsertAsync(DeviceSnapshot snapshot, CancellationToken cancellationToken) => _inner.UpsertAsync(snapshot, cancellationToken);
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => _inner.IsReadyAsync(cancellationToken);
    public Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return _inner.ListAsync(cancellationToken);
    }
}

internal sealed class ConnectionProbe : CircuitHandler
{
    private int _opened, _ups, _downs;
    public int Opened => Volatile.Read(ref _opened);
    public int ConnectionUps => Volatile.Read(ref _ups);
    public int ConnectionDowns => Volatile.Read(ref _downs);
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    { Interlocked.Increment(ref _opened); return Task.CompletedTask; }
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    { Interlocked.Increment(ref _ups); return Task.CompletedTask; }
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    { Interlocked.Increment(ref _downs); return Task.CompletedTask; }
}
