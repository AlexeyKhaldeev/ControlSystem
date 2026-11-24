using System.Globalization;
using System.Net;
using ControlSystem.Contracts;
using ControlSystem.Server.Components.Pages;
using ControlSystem.Server.Data;
using ControlSystem.Server.Monitoring;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlSystem.Tests;

public sealed class DashboardTests
{
    [Theory]
    [InlineData("en-US", "25.5%", "6.0 / 16.0 GiB", "100.0 / 500.0 GiB")]
    [InlineData("ru-RU", "25,5%", "6,0 / 16,0 GiB", "100,0 / 500,0 GiB")]
    public async Task Renders_actual_version_and_metrics_in_current_culture(string culture, string cpu, string memory, string disk)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var service = CreateService(new InMemoryDeviceStore(), clock);
            await service.ReceiveAsync(TestData.Heartbeat(clock.Now) with
            {
                AgentVersion = "0.1.7-preview",
                Telemetry = new(25.5, 16L << 30, 10L << 30, 90061, [new("C:\\", 500L << 30, 100L << 30)])
            }, CancellationToken.None);
            var html = await RenderAsync(service);
            Assert.Contains("v0.1.7-preview", html);
            Assert.DoesNotContain("@device.AgentVersion", html);
            Assert.Contains(cpu, html);
            Assert.Contains(memory, html);
            Assert.Contains(disk, html);
            Assert.Contains("1d 01h 01m", html);
            Assert.Contains("class=\"status online\">Online", html);
            Assert.Contains("Симулятор", html);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Renders_warmup_empty_disks_and_offline_snapshot_after_server_threshold()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var service = CreateService(new InMemoryDeviceStore(), clock);
        var request = TestData.Heartbeat(clock.Now);
        await service.ReceiveAsync(request with { Telemetry = request.Telemetry with { CpuUsagePercent = null, Disks = [] } }, CancellationToken.None);
        clock.Now = clock.Now.AddSeconds(35);
        Assert.Contains("class=\"status online\">Online", await RenderAsync(service));
        clock.Now = clock.Now.AddMilliseconds(1);
        var html = await RenderAsync(service);
        Assert.Contains("class=\"status offline\">Offline", html);
        Assert.Contains("Последний полученный снимок", html);
        Assert.Contains("Прогрев", html);
        Assert.Contains("<span>—</span>", html);
    }

    [Fact]
    public async Task Initial_read_failure_does_not_claim_no_heartbeat_was_received()
    {
        var html = await RenderAsync(CreateService(new FailingStore(), new ManualClock(DateTimeOffset.UtcNow)));
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("Ожидаем первый heartbeat", html);
    }

    [Fact]
    public async Task Refresh_replaces_metrics_and_status_and_recovers_after_read_failure()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new SwitchableStore();
        var service = CreateService(store, clock);
        await service.ReceiveAsync(TestData.Heartbeat(clock.Now), CancellationToken.None);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(service);
        services.AddSingleton<AntiforgeryStateProvider, TestAntiforgeryStateProvider>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new DashboardHtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(RefreshableHome), ParameterView.Empty);
            await root.QuiescenceTask;
            var component = Assert.IsType<RefreshableHome>(renderer.Home);
            clock.Now = clock.Now.AddSeconds(36);
            await component.RefreshForTestAsync();
            Assert.Contains("class=\"status offline\">Offline", root.ToHtmlString());
            store.Fail = true;
            await component.RefreshForTestAsync();
            Assert.Contains("role=\"alert\"", root.ToHtmlString());
            Assert.Contains("EXHIBIT-01", root.ToHtmlString());
            store.Fail = false;
            await service.ReceiveAsync(TestData.Heartbeat(clock.Now) with { Hostname = "UPDATED", AgentVersion = "0.2.9" }, CancellationToken.None);
            await component.RefreshForTestAsync();
            var html = root.ToHtmlString();
            Assert.Contains("UPDATED", html);
            Assert.Contains("v0.2.9", html);
            Assert.Contains("class=\"status online\">Online", html);
            Assert.DoesNotContain("role=\"alert\"", html);
        });
    }

    public sealed class RefreshableHome : Home
    {
        public async Task RefreshForTestAsync()
        {
            await base.OnInitializedAsync();
            StateHasChanged();
        }
    }

    private sealed class SwitchableStore : IDeviceStore
    {
        private readonly InMemoryDeviceStore _inner = new();
        public bool Fail { get; set; }
        public Task UpsertAsync(DeviceSnapshot snapshot, CancellationToken cancellationToken) => _inner.UpsertAsync(snapshot, cancellationToken);
        public Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken) =>
            Fail ? throw new InvalidOperationException("Store unavailable") : _inner.ListAsync(cancellationToken);
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(!Fail);
    }

    private static MonitoringService CreateService(IDeviceStore store, TimeProvider clock) =>
        new(store, clock, Options.Create(new MonitoringOptions()));

    private static async Task<string> RenderAsync(MonitoringService service)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(service);
        services.AddSingleton<AntiforgeryStateProvider, TestAntiforgeryStateProvider>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new DashboardHtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Home), ParameterView.Empty);
            await root.QuiescenceTask;
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    // Execute the existing component render tree without a browser/SignalR host.
    private sealed class DashboardHtmlRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : StaticHtmlRenderer(services, loggerFactory)
    {
        public Home? Home { get; private set; }
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode)
        {
            var component = componentActivator.CreateInstance(componentType);
            if (component is Home home) Home = home;
            return component;
        }
    }

    private sealed class TestAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    private sealed class FailingStore : IDeviceStore
    {
        public Task UpsertAsync(DeviceSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Store unavailable");
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
