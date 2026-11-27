using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ControlSystem.Contracts;
using ControlSystem.Server.Components;
using ControlSystem.Server.Data;
using ControlSystem.Server.Monitoring;
using ControlSystem.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ControlSystem.Server;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var app = CreateApp(args);
        if (app.Configuration.GetValue<bool>("Database:ApplyMigrations"))
        {
            if (!string.Equals(app.Configuration["Database:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Migrations require PostgreSql.");
            await using var scope = app.Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MonitoringDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            return;
        }
        await app.RunAsync();
    }

    public static WebApplication CreateApp(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });
        configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<DeviceStatus>()));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddOptions<SecurityOptions>().BindConfiguration("Security")
            .Validate(options => options.IsValid(), "Configure a unique SHA256 operator token hash and 1–100 unique agent IDs/token hashes.").ValidateOnStart();
        builder.Services.AddOptions<MonitoringOptions>().BindConfiguration("Monitoring")
            .Validate(options => options.OfflineAfterSeconds is >= 20 and <= 300, "OfflineAfterSeconds must be 20–300.").ValidateOnStart();
        builder.Services.AddOptions<HistoryRetentionOptions>().BindConfiguration("HistoryRetention")
            .Validate(options => options.IsValid(), "Invalid history retention days, batch size or interval.").ValidateOnStart();
        var provider = builder.Configuration["Database:Provider"] ?? "PostgreSql";
        if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
                throw new InvalidOperationException("InMemory storage is allowed only in Development/Testing.");
            builder.Services.AddSingleton<IDeviceStore, InMemoryDeviceStore>();
        }
        else if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            var connection = builder.Configuration.GetConnectionString("Monitoring")
                ?? throw new InvalidOperationException("Set ConnectionStrings__Monitoring for PostgreSQL.");
            builder.Services.AddDbContextFactory<MonitoringDbContext>(options => options.UseNpgsql(connection));
            builder.Services.AddSingleton<PostgresDeviceStore>();
            builder.Services.AddSingleton<IDeviceStore>(services => services.GetRequiredService<PostgresDeviceStore>());
            builder.Services.AddHostedService<HistoryRetentionService>();
        }
        else throw new InvalidOperationException("Database:Provider must be PostgreSql or InMemory.");
        builder.Services.AddScoped<MonitoringService>();
        builder.Services.AddSingleton<OperatorCircuitSessions>();
        builder.Services.AddScoped<OperatorCircuitCookieEvents>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.Cookie.Name = "__Host-ControlSystem";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.EventsType = typeof(OperatorCircuitCookieEvents);
            })
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Agent", p => p.AddAuthenticationSchemes(TokenAuthenticationHandler.SchemeName).RequireRole("Agent"));
            options.AddPolicy("OperatorApi", p => p.AddAuthenticationSchemes(TokenAuthenticationHandler.SchemeName).RequireRole("Operator"));
            options.AddPolicy("OperatorUi", p => p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireRole("Operator"));
        });
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddScoped<AuthenticationStateProvider, OperatorCircuitAuthenticationStateProvider>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("heartbeat", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        var app = builder.Build();
        app.UseExceptionHandler();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("HTTPS is required.");
                return;
            }
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            await next();
        });
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseAntiforgery();
        app.MapOperatorEndpoints();
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapGet("/health/ready", async (IDeviceStore store, CancellationToken ct) =>
        {
            try { return await store.IsReadyAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503); }
            catch (Exception error) when (error is not OperationCanceledException) { return Results.StatusCode(503); }
        }).AllowAnonymous();
        app.MapPost("/api/v1/heartbeats", async (HeartbeatRequest request, HttpContext context, MonitoringService service, TimeProvider clock, CancellationToken ct) =>
        {
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var deviceId) || deviceId != request.DeviceId)
                return Results.Forbid(authenticationSchemes: [TokenAuthenticationHandler.SchemeName]);
            var errors = HeartbeatValidator.Validate(request, clock.GetUtcNow());
            if (errors.Count != 0) return Results.ValidationProblem(errors);
            return Results.Ok(await service.ReceiveAsync(request, ct));
        }).RequireAuthorization("Agent").RequireRateLimiting("heartbeat");
        app.MapGet("/api/v1/devices", async (MonitoringService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequireAuthorization("OperatorApi");
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization("OperatorUi");
        return app;
    }
}
