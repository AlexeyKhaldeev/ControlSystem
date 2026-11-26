using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Text;
using static Microsoft.Playwright.Assertions;

namespace ControlSystem.BrowserTests;

public sealed class DashboardBrowserTests
{
    [Fact]
    public async Task Anonymous_browser_cannot_read_dashboard_api_or_negotiate()
    {
        await using var host = await BrowserHost.StartAsync();
        const string marker = "private-anonymous-marker";
        await host.PublishAsync(marker);
        await using var context = await host.CreateContextAsync();
        var page = await host.NewPageAsync(context);
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Вход оператора" })).ToBeVisibleAsync();
        Assert.Equal("/login", new Uri(page.Url).AbsolutePath);
        Assert.DoesNotContain(marker, await page.ContentAsync());
        await using var negotiate = await context.APIRequest.PostAsync("/_blazor/negotiate?negotiateVersion=1");
        await using var devices = await context.APIRequest.GetAsync("/api/v1/devices");
        Assert.Equal(401, negotiate.Status);
        Assert.Equal(401, devices.Status);
        Assert.DoesNotContain(marker, await negotiate.TextAsync());
        Assert.DoesNotContain(marker, await devices.TextAsync());
        Assert.Equal(0, host.Store.Reads);
    }

    [Fact]
    public async Task Operator_login_establishes_secure_cookie_and_live_dashboard_updates()
    {
        await using var host = await BrowserHost.StartAsync();
        await using var context = await host.CreateContextAsync();
        var page = await host.NewPageAsync(context);
        await host.LoginAsync(page);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Компьютеры экспозиции" })).ToBeVisibleAsync();
        var cookie = Assert.Single(await context.CookiesAsync(), c => c.Name == "__Host-ControlSystem");
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.Equal("/", cookie.Path);
        Assert.DoesNotContain("__Host-ControlSystem", await page.EvaluateAsync<string>("document.cookie"));
        await host.PublishAsync("live-after-login");
        await MarkerVisibleAsync(page, "live-after-login");
        await using var devices = await context.APIRequest.GetAsync("/api/v1/devices");
        Assert.Equal(401, devices.Status); // UI cookie never grants bearer-only API access.
        Assert.DoesNotContain(host.AgentToken, await page.ContentAsync());
        Assert.DoesNotContain(host.OperatorToken, await page.ContentAsync());
    }

    [Fact]
    public async Task Logout_revokes_open_dashboard_and_cookie_replay_and_stops_reads()
    {
        await using var host = await BrowserHost.StartAsync();
        await using var context = await host.CreateContextAsync();
        var dashboard = await host.NewPageAsync(context);
        var frames = new IncomingMarkerProbe(dashboard, "before-logout", "secret-after-logout");
        await host.LoginAsync(dashboard);
        await host.PublishAsync("before-logout");
        await MarkerVisibleAsync(dashboard, "before-logout");
        Assert.True(frames.Seen("before-logout"), "The incoming-frame observer must see the positive-control marker.");
        var savedCookies = await context.CookiesAsync();
        var logout = await host.NewPageAsync(context);
        await logout.GotoAsync("/");
        await logout.GetByRole(AriaRole.Button, new() { Name = "Выйти" }).ClickAsync();
        await Expect(logout.GetByRole(AriaRole.Heading, new() { Name = "Вход оператора" })).ToBeVisibleAsync();
        await SessionEndedAsync(dashboard);
        await host.PublishAsync("secret-after-logout");
        await AssertNoFurtherReadsAsync(host);
        await Expect(dashboard.GetByText("secret-after-logout", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(dashboard.Locator("table")).ToHaveCountAsync(0);
        Assert.False(frames.Seen("secret-after-logout"));

        await using var replay = await host.CreateContextAsync();
        await replay.AddCookiesAsync(savedCookies.Select(ToCookie));
        await using var negotiation = await replay.APIRequest.PostAsync("/_blazor/negotiate?negotiateVersion=1");
        Assert.Equal(401, negotiation.Status);
        var replayPage = await host.NewPageAsync(replay);
        await replayPage.GotoAsync("/");
        await Expect(replayPage.GetByRole(AriaRole.Heading, new() { Name = "Вход оператора" })).ToBeVisibleAsync();
        Assert.DoesNotContain("secret-after-logout", await replayPage.ContentAsync());
    }

    [Fact]
    public async Task Expired_cookie_invalidates_existing_circuit_and_stops_telemetry_reads()
    {
        await using var host = await BrowserHost.StartAsync();
        await using var context = await host.CreateContextAsync();
        var page = await host.NewPageAsync(context);
        var frames = new IncomingMarkerProbe(page, "before-expiry", "secret-after-expiry");
        await host.LoginAsync(page);
        await host.PublishAsync("before-expiry");
        await MarkerVisibleAsync(page, "before-expiry");
        Assert.True(frames.Seen("before-expiry"), "The incoming-frame observer must see the positive-control marker.");
        host.ExpireSession();
        await SessionEndedAsync(page);
        await host.PublishAsync("secret-after-expiry");
        await AssertNoFurtherReadsAsync(host);
        await using var negotiation = await context.APIRequest.PostAsync("/_blazor/negotiate?negotiateVersion=1");
        Assert.Equal(401, negotiation.Status);
        Assert.DoesNotContain("secret-after-expiry", await page.ContentAsync());
        await Expect(page.Locator("table")).ToHaveCountAsync(0);
        Assert.False(frames.Seen("secret-after-expiry"));
    }

    [Fact]
    public async Task Signalr_reconnect_restores_live_updates_without_document_reload()
    {
        await using var host = await BrowserHost.StartAsync();
        await using var context = await host.CreateContextAsync();
        var page = await host.NewPageAsync(context);
        await InstallSocketCaptureAsync(page);
        await host.LoginAsync(page);
        await host.PublishAsync("before-reconnect");
        await MarkerVisibleAsync(page, "before-reconnect");
        await page.EvaluateAsync("globalThis.__e2eDocumentSentinel = 'retained-document'");
        var opened = host.Probe.Opened;
        var connectionUps = host.Probe.ConnectionUps;
        var socketCount = await page.EvaluateAsync<int>("globalThis.__e2eSockets.length");
        await CloseTransportAsync(page);
        await page.WaitForFunctionAsync("count => globalThis.__e2eSockets.length > count && globalThis.__e2eSockets.some(s => s.readyState === 1)", socketCount, new() { Timeout = 30_000 });
        await host.PublishAsync("after-reconnect");
        await MarkerVisibleAsync(page, "after-reconnect");
        Assert.Equal("retained-document", await page.EvaluateAsync<string>("globalThis.__e2eDocumentSentinel"));
        Assert.Equal(opened, host.Probe.Opened);
        Assert.True(host.Probe.ConnectionUps > connectionUps, "The existing circuit must reconnect.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnected_dashboard_cannot_resume_telemetry_after_session_ends(bool expire)
    {
        await using var host = await BrowserHost.StartAsync();
        await using var context = await host.CreateContextAsync();
        var page = await host.NewPageAsync(context);
        await InstallSocketCaptureAsync(page);
        var frames = new IncomingMarkerProbe(page, "before-disconnected-logout", "secret-while-disconnected");
        await host.LoginAsync(page);
        await host.PublishAsync("before-disconnected-logout");
        await MarkerVisibleAsync(page, "before-disconnected-logout");
        Assert.True(frames.Seen("before-disconnected-logout"), "The incoming-frame observer must see the positive-control marker.");
        await using var logoutContext = await host.CreateContextAsync();
        await logoutContext.AddCookiesAsync((await context.CookiesAsync()).Select(ToCookie));
        var logout = await host.NewPageAsync(logoutContext);
        await logout.GotoAsync("/");
        await page.EvaluateAsync("globalThis.__e2eDocumentSentinel = 'offline-document'");
        await context.SetOfflineAsync(true);
        await CloseTransportAsync(page);
        if (expire) host.ExpireSession();
        else
        {
            await logout.GetByRole(AriaRole.Button, new() { Name = "Выйти" }).ClickAsync();
            await Expect(logout.GetByRole(AriaRole.Heading, new() { Name = "Вход оператора" })).ToBeVisibleAsync();
        }
        // The offline document can retain previously delivered data. New data must not arrive.
        await host.PublishAsync("secret-while-disconnected");
        await AssertNoFurtherReadsAsync(host);
        await context.SetOfflineAsync(false);
        await using var negotiation = await context.APIRequest.PostAsync("/_blazor/negotiate?negotiateVersion=1");
        Assert.Equal(401, negotiation.Status);
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
        Assert.Equal("offline-document", await page.EvaluateAsync<string>("globalThis.__e2eDocumentSentinel"));
        Assert.DoesNotContain("secret-while-disconnected", await page.ContentAsync());
        Assert.False(frames.Seen("secret-while-disconnected"));
    }

    [Fact]
    public async Task Logout_does_not_revoke_an_independently_authenticated_operator_session()
    {
        await using var host = await BrowserHost.StartAsync();
        await using var firstContext = await host.CreateContextAsync();
        await using var secondContext = await host.CreateContextAsync();
        var first = await host.NewPageAsync(firstContext);
        var second = await host.NewPageAsync(secondContext);
        await host.LoginAsync(first);
        await host.LoginAsync(second); // A genuine second login creates a separate server session.
        await host.PublishAsync("independent-before-logout");
        await MarkerVisibleAsync(first, "independent-before-logout");
        await MarkerVisibleAsync(second, "independent-before-logout");
        await first.GetByRole(AriaRole.Button, new() { Name = "Выйти" }).ClickAsync();
        await Expect(first.GetByRole(AriaRole.Heading, new() { Name = "Вход оператора" })).ToBeVisibleAsync();
        await host.PublishAsync("independent-after-logout");
        await MarkerVisibleAsync(second, "independent-after-logout");
        await Expect(second.GetByText("Сессия завершена.", new() { Exact = false })).ToHaveCountAsync(0);
        await using var negotiation = await secondContext.APIRequest.PostAsync("/_blazor/negotiate?negotiateVersion=1");
        Assert.Equal(200, negotiation.Status);
    }

    [Fact]
    public async Task Browser_rejects_an_untrusted_localhost_certificate()
    {
        await using var host = await BrowserHost.StartAsync(untrustedTls: true);
        await using var context = await host.CreateContextAsync();
        var page = await host.NewPageAsync(context);
        var error = await Assert.ThrowsAsync<PlaywrightException>(() => page.GotoAsync("/login"));
        Assert.Contains("ERR_CERT_AUTHORITY_INVALID", error.Message);
        Assert.Equal(0, host.Store.Reads);
    }

    private static Cookie ToCookie(BrowserContextCookiesResult cookie) => new()
    {
        Name = cookie.Name, Value = cookie.Value, Domain = cookie.Domain, Path = cookie.Path,
        Expires = cookie.Expires, HttpOnly = cookie.HttpOnly, Secure = cookie.Secure, SameSite = cookie.SameSite
    };

    private static Task MarkerVisibleAsync(IPage page, string marker) =>
        Expect(page.Locator("table tbody").GetByText(marker, new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 15_000 });

    private static Task SessionEndedAsync(IPage page) =>
        Expect(page.GetByText("Сессия завершена.", new() { Exact = false })).ToBeVisibleAsync(new() { Timeout = 10_000 });

    private static async Task AssertNoFurtherReadsAsync(BrowserHost host)
    {
        var reads = host.Store.Reads;
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken); // Cross a real five-second Home refresh tick.
        Assert.Equal(reads, host.Store.Reads);
    }

    // Retain only marker matches, never raw frames, cookies, or credentials.
    private sealed class IncomingMarkerProbe
    {
        private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

        public IncomingMarkerProbe(IPage page, params string[] markers)
        {
            var patterns = markers.Select(marker => (Marker: marker,
                Utf8: Encoding.UTF8.GetBytes(marker), Utf16: Encoding.Unicode.GetBytes(marker))).ToArray();
            page.WebSocket += (_, socket) =>
            {
                if (new Uri(socket.Url).AbsolutePath != "/_blazor") return;
                socket.FrameReceived += (_, frame) =>
                {
                    var bytes = frame.Binary;
                    foreach (var pattern in patterns)
                        if (bytes.AsSpan().IndexOf(pattern.Utf8) >= 0 || bytes.AsSpan().IndexOf(pattern.Utf16) >= 0)
                            _seen.TryAdd(pattern.Marker, 0);
                };
            };
        }

        public bool Seen(string marker) => _seen.ContainsKey(marker);
    }

    private static Task InstallSocketCaptureAsync(IPage page) => page.AddInitScriptAsync("""
        globalThis.__e2eSockets = [];
        const NativeWebSocket = globalThis.WebSocket;
        globalThis.WebSocket = new Proxy(NativeWebSocket, {
          construct(target, args) {
            const socket = Reflect.construct(target, args);
            if (new URL(args[0], location.href).pathname === '/_blazor')
              globalThis.__e2eSockets.push(socket);
            return socket;
          }
        });
        """);

    private static async Task CloseTransportAsync(IPage page)
    {
        // Close the actual browser-owned WebSocket, not a simulated server connection.
        // Resolve from its close event so an offline flag alone cannot produce a false pass.
        await page.EvaluateAsync("""
            async () => {
              const socket = globalThis.__e2eSockets.findLast(s => s.readyState < 2);
              if (!socket) {
                if (globalThis.__e2eSockets.some(s => s.readyState === 3)) return;
                throw new Error('No live Blazor WebSocket was observed');
              }
              await new Promise((resolve, reject) => {
                const timeout = setTimeout(() => reject(new Error('WebSocket did not close')), 10000);
                socket.addEventListener('close', () => { clearTimeout(timeout); resolve(); }, { once: true });
                socket.close(4001, 'E2E transport interruption');
              });
            }
            """);
    }
}
