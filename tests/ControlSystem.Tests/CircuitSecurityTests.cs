using ControlSystem.Server.Components.Pages;
using ControlSystem.Server.Monitoring;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using System.Security.Claims;
using ControlSystem.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServerProgram = ControlSystem.Server.Program;

namespace ControlSystem.Tests;

public sealed class CircuitSecurityTests
{
    // Uses the application's real cookie handler and circuit authentication-state provider.
    // It deliberately does not claim to exercise a browser, WebSocket, or rendered component.
    [Fact]
    public async Task Circuit_authentication_is_invalidated_after_real_cookie_expires()
    {
        await using var app = CreateApp();
        var cookie = await SignInAsync(app, DateTimeOffset.UtcNow.AddSeconds(2));
        var principal = await AuthenticateAsync(app, cookie);
        Assert.NotNull(principal);
        await using var circuit = app.Services.CreateAsyncScope();
        var provider = circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Null(await AuthenticateAsync(app, cookie));
        await WaitForAnonymousAsync(provider);
    }

    [Fact]
    public async Task Logout_revokes_replayed_cookie_and_existing_circuit_authentication()
    {
        await using var app = CreateApp();
        var cookie = await SignInAsync(app, DateTimeOffset.UtcNow.AddMinutes(5));
        var principal = await AuthenticateAsync(app, cookie);
        Assert.NotNull(principal);
        await using var circuit = app.Services.CreateAsyncScope();
        var provider = circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        await using (var logout = app.Services.CreateAsyncScope())
        {
            var context = Context(logout.ServiceProvider, cookie);
            context.User = principal;
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            Assert.Contains("expires=", context.Response.Headers.SetCookie.ToString());
        }
        Assert.Null(await AuthenticateAsync(app, cookie));
        await WaitForAnonymousAsync(provider);
    }

    [Fact]
    public void Session_validation_fails_closed_and_expiry_is_not_extended()
    {
        var clock = new SessionClock();
        var sessions = new OperatorCircuitSessions(clock);
        var expires = clock.GetUtcNow().AddMinutes(5);
        var principal = SessionPrincipal(sessions.Create(expires));
        Assert.True(sessions.IsValid(principal));
        clock.UtcNow = expires.AddTicks(-1);
        Assert.True(sessions.IsValid(principal));
        clock.UtcNow = expires;
        Assert.False(sessions.IsValid(principal));
        clock.UtcNow = expires.AddMinutes(-1);
        Assert.False(sessions.IsValid(principal)); // Removed on expiry; rolling time back cannot resurrect it.
        Assert.False(sessions.IsValid(SessionPrincipal(null)));
        Assert.False(sessions.IsValid(SessionPrincipal("unknown-session")));
        Assert.False(new OperatorCircuitSessions(clock).IsValid(principal)); // Restart fails closed.
        Assert.Throws<ArgumentOutOfRangeException>(() => sessions.Create(clock.GetUtcNow()));
    }

    [Fact]
    public void Revocation_is_per_session_and_invalid_identity_cannot_use_an_existing_id()
    {
        var clock = new SessionClock();
        var sessions = new OperatorCircuitSessions(clock);
        var first = SessionPrincipal(sessions.Create(clock.GetUtcNow().AddMinutes(5)));
        var second = SessionPrincipal(sessions.Create(clock.GetUtcNow().AddMinutes(5)));
        sessions.Revoke(first);
        Assert.False(sessions.IsValid(first));
        Assert.True(sessions.IsValid(second));
        var id = second.FindFirstValue(OperatorCircuitSessions.SessionIdClaimType)!;
        var agent = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(OperatorCircuitSessions.SessionIdClaimType, id), new Claim(ClaimTypes.Role, "Agent")
        ], "Bearer"));
        Assert.False(sessions.IsValid(agent));
        ((ClaimsIdentity)second.Identity!).AddClaim(new Claim(OperatorCircuitSessions.SessionIdClaimType, id));
        Assert.False(sessions.IsValid(second));
    }

    private static ClaimsPrincipal SessionPrincipal(string? id)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "Operator")], CookieAuthenticationDefaults.AuthenticationScheme);
        if (id is not null) identity.AddClaim(new Claim(OperatorCircuitSessions.SessionIdClaimType, id));
        return new ClaimsPrincipal(identity);
    }

    private sealed class SessionClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [Theory]
    [InlineData(0)] // Revoke before read.
    [InlineData(1)] // Revoke during read.
    [InlineData(2)] // Positive render control: valid session.
    public async Task Home_enforces_session_at_both_sides_of_the_store_read(int scenario)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddAuthorization(options => options.AddPolicy("OperatorUi", policy => policy.RequireRole("Operator")));
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<OperatorCircuitSessions>();
        builder.Services.AddScoped<AuthenticationStateProvider, Microsoft.AspNetCore.Components.Server.ServerAuthenticationStateProvider>();
        var store = new BlockingStore();
        builder.Services.AddSingleton<IDeviceStore>(store);
        builder.Services.AddScoped<MonitoringService>();
        builder.Services.Configure<MonitoringOptions>(_ => { });
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<OperatorCircuitSessions>();
        var principal = SessionPrincipal(sessions.Create(DateTimeOffset.UtcNow.AddMinutes(5)));
        var provider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        if (scenario == 0) sessions.Revoke(principal);
        await using var renderer = new TestHtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        var rendering = renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Home), ParameterView.Empty);
            await root.QuiescenceTask;
            return root;
        });
        if (scenario != 0)
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (scenario == 1) sessions.Revoke(principal);
            store.Complete.SetResult([DeviceSnapshot.From(TestData.Heartbeat() with { Hostname = "secret-after-revoke" }, DateTimeOffset.UtcNow)]);
        }
        var root = await rendering.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        if (scenario == 2) Assert.Contains("secret-after-revoke", html);
        else Assert.DoesNotContain("secret-after-revoke", html);
        Assert.Equal(scenario == 0 ? 0 : 1, store.Reads);
    }

    // Test-only renderer accepts the component's server render-mode marker. It exercises
    // Home's lifecycle/rendering and async store boundary, not SignalR or a browser.
    private sealed class TestHtmlRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode) => componentActivator.CreateInstance(componentType);
    }

    private sealed class BlockingStore : IDeviceStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<DeviceSnapshot>> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }
        public Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken)
        {
            Reads++;
            Entered.SetResult();
            return Complete.Task.WaitAsync(cancellationToken);
        }
        public Task UpsertAsync(DeviceSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static async Task WaitForAnonymousAsync(AuthenticationStateProvider provider)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!(await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated) return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
    }

    private static async Task<string> SignInAsync(WebApplication app, DateTimeOffset expires)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = Context(scope.ServiceProvider);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, "Operator")
        ], CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { ExpiresUtc = expires });
        return context.Response.Headers.SetCookie.ToString().Split(';')[0];
    }

    private static async Task<ClaimsPrincipal?> AuthenticateAsync(WebApplication app, string cookie)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var result = await Context(scope.ServiceProvider, cookie).AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return result.Succeeded ? result.Principal : null;
    }

    private static DefaultHttpContext Context(IServiceProvider services, string? cookie = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        return context;
    }

    private static WebApplication CreateApp() => ServerProgram.CreateApp(["--environment", "Testing"], builder =>
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "InMemory",
            ["Security:OperatorTokenHash"] = TokenDigest.Hash("circuit-test-operator-token-111111111111111111"),
            ["Security:Agents:0:DeviceId"] = TestData.DeviceId.ToString(),
            ["Security:Agents:0:TokenHash"] = TokenDigest.Hash("circuit-test-agent-token-000000000000000000000")
        });
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    });
}
