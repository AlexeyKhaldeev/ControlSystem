using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace ControlSystem.Agent.Telemetry;
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
public readonly record struct PhysicalMemory(ulong Total, ulong Available);
public interface IWindowsDisk
{
    string Name { get; }
    DriveType DriveType { get; }
    bool IsReady { get; }
    long TotalSize { get; }
    long TotalFreeSpace { get; }
}
public interface IWindowsTelemetrySource
{
    CpuTimes? ReadCpuTimes();
    PhysicalMemory ReadMemory();
    ulong ReadUptimeMilliseconds();
    IEnumerable<IWindowsDisk> GetDrives();
}
[SupportedOSPlatform("windows")]
public sealed class WindowsTelemetrySource : IWindowsTelemetrySource
{
    public CpuTimes? ReadCpuTimes()
    {
        // GetSystemTimes measures only the calling processor group with >64 CPUs.
        if (GetActiveProcessorCount(0xffff) > 64) return null;
        return GetSystemTimes(out var idle, out var kernel, out var user) ? new(idle, kernel, user) : null;
    }
    public PhysicalMemory ReadMemory()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(memory.TotalPhysical, memory.AvailablePhysical);
    }
    public ulong ReadUptimeMilliseconds() => GetTickCount64();
    public IEnumerable<IWindowsDisk> GetDrives() => DriveInfo.GetDrives().Select(drive => new WindowsDisk(drive));
    private sealed class WindowsDisk(DriveInfo drive) : IWindowsDisk
    {
        public string Name => drive.Name;
        public DriveType DriveType => drive.DriveType;
        public bool IsReady => drive.IsReady;
        public long TotalSize => drive.TotalSize;
        public long TotalFreeSpace => drive.TotalFreeSpace;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
