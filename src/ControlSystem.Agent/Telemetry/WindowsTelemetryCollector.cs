using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlSystem.Contracts;
namespace ControlSystem.Agent.Telemetry;
public sealed class WindowsTelemetryCollector : ITelemetryCollector
{
    private readonly ILogger<WindowsTelemetryCollector> _logger;
    private readonly IWindowsTelemetrySource _source;
    private CpuTimes? _previous;
    private bool _cpuUnavailable;
    [SupportedOSPlatform("windows")]
    public WindowsTelemetryCollector(ILogger<WindowsTelemetryCollector> logger) : this(logger, new WindowsTelemetrySource()) { }
    public WindowsTelemetryCollector(ILogger<WindowsTelemetryCollector> logger, IWindowsTelemetrySource source)
    { _logger = logger; _source = source; }
    public bool IsSimulated => false;
    public string Hostname => Environment.MachineName;
    public string OperatingSystemName => RuntimeInformation.OSDescription;
    public Task<TelemetrySnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _source.ReadCpuTimes();
        double? cpu = null;
        if (current is { } c && _previous is { } p && c.Kernel >= p.Kernel && c.User >= p.User && c.Idle >= p.Idle)
        {
            var total = (double)(c.Kernel - p.Kernel) + (c.User - p.User);
            var idle = c.Idle - p.Idle;
            if (total > 0 && idle <= total) cpu = Math.Clamp(100d * (total - idle) / total, 0, 100);
        }
        _previous = current;
        if (current is null && !_cpuUnavailable)
            _logger.LogWarning("System CPU metric unavailable (native sample failure or more than 64 logical processors).");
        _cpuUnavailable = current is null;
        var memory = _source.ReadMemory();
        if (memory.Total == 0 || memory.Total > long.MaxValue || memory.Available > memory.Total)
            throw new IOException("Invalid physical memory sample.");
        var uptime = _source.ReadUptimeMilliseconds() / 1000;
        if (uptime > 3_155_760_000) throw new IOException("Invalid system uptime sample.");
        var disks = new List<DiskTelemetry>();
        foreach (var drive in _source.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disks.Count == MonitoringProtocol.MaxDisks) break;
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var total = drive.TotalSize;
                var free = drive.TotalFreeSpace;
                if (total > 0 && free >= 0 && free <= total && drive.Name.Length <= 200)
                    disks.Add(new(drive.Name, total, free));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _logger.LogWarning("Cannot read disk {Drive}: {ErrorType}", drive.Name, error.GetType().Name); }
        }
        return Task.FromResult(new TelemetrySnapshot(cpu, (long)memory.Total, (long)memory.Available, (long)uptime, disks));
    }
}
