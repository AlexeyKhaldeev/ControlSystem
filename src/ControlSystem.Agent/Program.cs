using ControlSystem.Agent;
using ControlSystem.Agent.Telemetry;
using ControlSystem.Agent.Transport;
using Microsoft.Extensions.Options;

var simulated = args.Contains("--simulate", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args.Where(a => a != "--simulate").ToArray(),
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddOptions<AgentOptions>().BindConfiguration("Agent")
    .Validate(options => options.IsValid(), "Configure an HTTPS root ServerUrl, stable DeviceId and a token of at least 32 characters.").ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
if (simulated) builder.Services.AddSingleton<ITelemetryCollector, SimulatedTelemetryCollector>();
else if (OperatingSystem.IsWindows()) builder.Services.AddSingleton<ITelemetryCollector, WindowsTelemetryCollector>();
else throw new PlatformNotSupportedException("Real telemetry requires Windows. Pass --simulate explicitly on macOS/Linux.");
builder.Services.AddSingleton(provider => AgentHttpClient.Create(provider.GetRequiredService<IOptions<AgentOptions>>().Value));
builder.Services.AddSingleton<IHeartbeatClient, HeartbeatClient>();
builder.Services.AddHostedService<TelemetryWorker>();
#if WINDOWS_SERVICE
builder.Services.AddWindowsService(options => options.ServiceName = "ControlSystem.Agent");
builder.Logging.AddEventLog(settings =>
{
    if (OperatingSystem.IsWindows()) settings.SourceName = "ControlSystem.Agent";
});
#endif

using var host = builder.Build();
await host.RunAsync();
