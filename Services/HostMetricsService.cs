using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace DedicatedServerStudio.Services;

public sealed class HostMetricsService : BackgroundService
{
    private readonly object _sync = new();
    private HostMetricsSnapshot _snapshot = new(Environment.MachineName, Environment.ProcessorCount, 0, 0, 0, 0, 0, [], 0, 0, DateTimeOffset.UtcNow);
    private CpuTimes? _previousCpu;
    private NetworkTotals? _previousNetwork;
    private DateTimeOffset _previousNetworkSampleAt;

    public HostMetricsSnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Sample(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private void Sample()
    {
        var cpu = ReadCpu();
        var cpuPercent = 0d;
        var (totalMemory, availableMemory) = ReadMemory();
        var usedMemory = Math.Max(0, totalMemory - availableMemory);
        var usedMemoryPercent = totalMemory == 0 ? 0 : Math.Round(100d * usedMemory / totalMemory, 1);
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => new HostDriveMetrics(drive.Name, drive.TotalSize, drive.TotalSize - drive.AvailableFreeSpace,
                drive.TotalSize == 0 ? 0 : Math.Round(100d * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize, 1)))
            .ToArray();
        var processMemory = Process.GetCurrentProcess().WorkingSet64;
        var network = ReadNetworkTotals();

        lock (_sync)
        {
            if (cpu is { } current && _previousCpu is { } previous)
            {
                var totalDelta = (current.Kernel - previous.Kernel) + (current.User - previous.User);
                var idleDelta = current.Idle - previous.Idle;
                if (totalDelta > 0) cpuPercent = Math.Round(Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100), 1);
            }
            if (cpu is { } sampled) _previousCpu = sampled;
            var sampledAt = DateTimeOffset.UtcNow;
            var receivedBytesPerSecond = 0d;
            var sentBytesPerSecond = 0d;
            if (network is { } currentNetwork)
            {
                if (_previousNetwork is { } previousNetwork)
                {
                    var elapsed = (sampledAt - _previousNetworkSampleAt).TotalSeconds;
                    if (elapsed > 0)
                    {
                        receivedBytesPerSecond = (currentNetwork.Received >= previousNetwork.Received ? currentNetwork.Received - previousNetwork.Received : 0) / elapsed;
                        sentBytesPerSecond = (currentNetwork.Sent >= previousNetwork.Sent ? currentNetwork.Sent - previousNetwork.Sent : 0) / elapsed;
                    }
                }
                _previousNetwork = currentNetwork;
                _previousNetworkSampleAt = sampledAt;
            }
            _snapshot = new HostMetricsSnapshot(Environment.MachineName, Environment.ProcessorCount, cpuPercent,
                totalMemory, usedMemory, usedMemoryPercent, processMemory, drives,
                Math.Round(receivedBytesPerSecond, 1), Math.Round(sentBytesPerSecond, 1), sampledAt);
        }
    }

    private static CpuTimes? ReadCpu()
    {
        if (OperatingSystem.IsWindows())
            return GetSystemTimes(out var idle, out var kernel, out var user)
                ? new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user))
                : null;
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var values = File.ReadLines("/proc/stat").FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values is not { Length: >= 8 } || values[0] != "cpu") return null;
            var ticks = values.Skip(1).Take(8).Select(value => ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var userTicks = ticks[0] + ticks[1];
            var idleTicks = ticks[3] + ticks[4];
            var totalTicks = ticks.Aggregate(0UL, (total, value) => total + value);
            return new CpuTimes(idleTicks, totalTicks - userTicks, userTicks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException) { return null; }
    }

    private static (ulong Total, ulong Available) ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref memory) ? (memory.TotalPhysical, memory.AvailablePhysical) : (0, 0);
        }
        if (!OperatingSystem.IsLinux()) return (0, 0);
        try
        {
            ulong total = 0, available = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = ReadKilobytes(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = ReadKilobytes(line);
                if (total > 0 && available > 0) break;
            }
            return (total, Math.Min(total, available));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException) { return (0, 0); }
    }

    private static ulong ReadKilobytes(string line)
    {
        var value = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        return checked(ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture) * 1024);
    }

    private static NetworkTotals? ReadNetworkTotals()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(adapter => adapter.GetIPv4Statistics())
                .ToArray();
            return new NetworkTotals(
                adapters.Aggregate(0UL, (sum, statistics) => sum + (ulong)Math.Max(0, statistics.BytesReceived)),
                adapters.Aggregate(0UL, (sum, statistics) => sum + (ulong)Math.Max(0, statistics.BytesSent)));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { return null; }
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
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

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
    private readonly record struct NetworkTotals(ulong Received, ulong Sent);
}

public sealed record HostDriveMetrics(string Name, long TotalBytes, long UsedBytes, double UsedPercent);
public sealed record HostMetricsSnapshot(string MachineName, int LogicalProcessors, double CpuPercent, ulong TotalMemoryBytes,
    ulong UsedMemoryBytes, double UsedMemoryPercent, long DssWorkingSetBytes, IReadOnlyList<HostDriveMetrics> Drives,
    double NetworkReceivedBytesPerSecond, double NetworkSentBytesPerSecond, DateTimeOffset SampledAt);
