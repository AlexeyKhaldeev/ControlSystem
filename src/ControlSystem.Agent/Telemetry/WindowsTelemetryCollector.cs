using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlSystem.Contracts;

namespace ControlSystem.Agent.Telemetry;

[SupportedOSPlatform("windows")]
public sealed class WindowsTelemetryCollector(ILogger<WindowsTelemetryCollector> logger) : ITelemetryCollector
{
    private (ulong Idle, ulong Kernel, ulong User)? _previous;
    public bool IsSimulated => false;
    public string Hostname => Environment.MachineName;
    public string OperatingSystemName => RuntimeInformation.OSDescription;

    public Task<TelemetrySnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        double? cpu = null;
        if (_previous is { } p && kernel >= p.Kernel && user >= p.User && idle >= p.Idle)
        {
            var total = (kernel - p.Kernel) + (user - p.User);
            if (total > 0) cpu = Math.Clamp(100d * (total - Math.Min(total, idle - p.Idle)) / total, 0, 100);
        }
        _previous = (idle, kernel, user);
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var disks = new List<DiskTelemetry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady && drive.TotalSize > 0)
                    disks.Add(new(drive.Name, drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Cannot read disk {Drive}: {ErrorType}", drive.Name, error.GetType().Name);
            }
        }
        return Task.FromResult(new TelemetrySnapshot(cpu, checked((long)memory.TotalPhysical), checked((long)memory.AvailablePhysical),
            checked((long)(GetTickCount64() / 1000)), disks.Take(MonitoringProtocol.MaxDisks).ToArray()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
