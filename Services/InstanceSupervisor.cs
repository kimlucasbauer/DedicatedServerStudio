using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed class InstanceSupervisor(StateStore state, GameAdapterRegistry adapters)
{
    private static readonly JsonSerializerOptions BridgeJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, IManagedServerProcess> _processes = new();
    private readonly ConcurrentDictionary<Guid, string> _logPaths = new();
    private readonly ConcurrentDictionary<Guid, ConsoleLogBuffer> _logs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _operations = new();
    private readonly object _processMetricsLock = new();
    private readonly Dictionary<Guid, (int ProcessId, long CpuTicks, long Timestamp)> _processMetricSamples = [];
    private readonly ConcurrentDictionary<Guid, (long SampledAt, long Bytes)> _storageUsageCache = new();

    public string GetStatus(Guid id) => _processes.TryGetValue(id, out var process) && !process.HasExited ? "running" : "stopped";
    public bool IsConsoleConnected(Guid id) => _processes.TryGetValue(id, out var process) && !process.HasExited && process.ConsoleConnected;

    public async Task RecoverRunningProcessesAsync()
    {
        var profiles = await state.ReadAsync(data => data.Instances
            .SelectMany(profile => profile.Servers.Count == 0 ? [profile] : profile.Servers.Select(profile.WithServer))
            .ToArray());
        var udpOwners = OperatingSystem.IsWindows() ? GetUdpPortOwners() : new Dictionary<int, int>();
        foreach (var profile in profiles)
        {
            var instanceDirectory = InstanceDirectory(profile.Id);
            var statusPath = Path.Combine(instanceDirectory, "console-bridge.json");
            var recovered = false;
            if (File.Exists(statusPath))
            {
                try
                {
                    var status = JsonSerializer.Deserialize<ConsoleBridgeStatus>(await File.ReadAllTextAsync(statusPath), BridgeJsonOptions);
                    if (status is not null)
                    {
                        var managed = await ConsoleBridgeManagedProcess.TryRecoverAsync(ConsolePipeName(profile.Id), status);
                        if (managed is not null)
                        {
                            RegisterRecoveredProcess(profile.Id, instanceDirectory, managed, status.LogPath);
                            recovered = true;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A stale bridge record is expected after an unclean DSS shutdown. Try to
                    // identify the still-running game process by its bound server port below.
                }
            }

            if (recovered)
                continue;

            TryDelete(statusPath);
            if (!TryRecoverLegacyProcess(profile, udpOwners, out var legacy))
                continue;

            // Older DSS versions owned anonymous pipes in the web process. Those handles cannot
            // be reopened after that process exits, but tracking the existing server avoids a
            // second launch and still allows a clean Ctrl+Break shutdown.
            RegisterRecoveredProcess(profile.Id, instanceDirectory, legacy);
        }
    }

    private void RegisterRecoveredProcess(Guid id, string instanceDirectory, IManagedServerProcess process, string? activeLogPath = null)
    {
        _processes[id] = process;
        var legacyPath = Path.Combine(instanceDirectory, "server.log");
        var logPath = !string.IsNullOrWhiteSpace(activeLogPath) && Path.IsPathFullyQualified(activeLogPath)
            ? activeLogPath
            : File.Exists(legacyPath) ? legacyPath : state.GetServerLogPath(id);
        _logPaths[id] = logPath;
        _logs[id] = new ConsoleLogBuffer(logPath);
        _ = ObserveExitAsync(id, logPath, process);
    }

    private bool TryRecoverLegacyProcess(ServerProfile profile, IReadOnlyDictionary<int, int> udpOwners, out IManagedServerProcess process)
    {
        process = null!;
        if (!adapters.TryGet(profile.GameId, out var adapter) || string.IsNullOrWhiteSpace(profile.InstallPath))
            return false;

        var expectedExecutable = Path.GetFullPath(Path.Combine(profile.InstallPath, adapter.ExecutableName));
        for (var port = profile.Port; port < profile.Port + Math.Max(1, adapter.PortSpan); port++)
        {
            if (!udpOwners.TryGetValue(port, out var processId)) continue;
            try
            {
                var candidate = Process.GetProcessById(processId);
                if (candidate.HasExited || !string.Equals(Path.GetFullPath(candidate.MainModule?.FileName ?? ""), expectedExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Dispose();
                    continue;
                }
                process = new RecoveredLegacyServerProcess(candidate);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The process can exit while startup recovery is checking its UDP table entry.
            }
        }
        return false;
    }

    public ManagedProcessMetrics? GetProcessMetrics(Guid id)
    {
        if (!_processes.TryGetValue(id, out var managed) || managed.HasExited)
        {
            lock (_processMetricsLock) _processMetricSamples.Remove(id);
            return null;
        }

        try
        {
            var process = managed.Process;
            process.Refresh();
            if (process.HasExited)
            {
                lock (_processMetricsLock) _processMetricSamples.Remove(id);
                return null;
            }

            var processId = process.Id;
            var cpuTicks = process.TotalProcessorTime.Ticks;
            var timestamp = Stopwatch.GetTimestamp();
            var cpuPercent = 0d;
            lock (_processMetricsLock)
            {
                if (_processMetricSamples.TryGetValue(id, out var previous) && previous.ProcessId == processId)
                {
                    var elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, timestamp).TotalSeconds;
                    var cpuDelta = cpuTicks - previous.CpuTicks;
                    if (elapsed > 0 && cpuDelta >= 0)
                        cpuPercent = Math.Round(Math.Clamp(100d * TimeSpan.FromTicks(cpuDelta).TotalSeconds / elapsed / Math.Max(1, Environment.ProcessorCount), 0, 100), 1);
                }
                _processMetricSamples[id] = (processId, cpuTicks, timestamp);
            }

            var uptime = Math.Max(0, (DateTimeOffset.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds);
            var (ioReadBytes, ioWriteBytes) = GetIoBytes(process);
            return new ManagedProcessMetrics(processId, process.WorkingSet64, cpuPercent, Math.Round(uptime), ioReadBytes, ioWriteBytes, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            lock (_processMetricsLock) _processMetricSamples.Remove(id);
            return null;
        }
    }

    public IReadOnlyList<string> GetLogLines(Guid id, int count = 200)
    {
        var path = _logPaths.GetOrAdd(id, state.GetServerLogPath);
        if (!File.Exists(path)) return [];
        try { return File.ReadLines(path).TakeLast(Math.Clamp(count, 1, 1000)).ToArray(); }
        catch (IOException) { return []; }
    }

    public string? GetConsoleLogPath(Guid id)
    {
        var path = _logPaths.GetOrAdd(id, state.GetServerLogPath);
        return File.Exists(path) ? path : null;
    }

    public (IReadOnlyList<ConsoleLogEntry> Entries, long Sequence) GetConsoleEntries(Guid id, long afterSequence = 0, int count = 300)
    {
        if (!_logs.TryGetValue(id, out var buffer))
        {
            var path = _logPaths.GetOrAdd(id, state.GetServerLogPath);
            buffer = new ConsoleLogBuffer(path);
            buffer = _logs.GetOrAdd(id, buffer);
        }
        if (_logPaths.TryGetValue(id, out var logPath)) buffer.RefreshFromFile(logPath);
        return buffer.Read(afterSequence, Math.Clamp(count, 1, 1000));
    }

    public async Task<(bool Success, string Message)> StartAsync(ServerProfile profile)
    {
        var gate = _operations.GetOrAdd(profile.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_processes.TryGetValue(profile.Id, out var old) && !old.HasExited)
                return (false, "This profile is already running under this manager.");
            if (!adapters.TryGet(profile.GameId, out var adapter))
                return (false, $"There is no server adapter registered for game '{profile.GameId}'.");

            var serverExe = Path.Combine(profile.InstallPath, adapter.ExecutableName);
            if (!Directory.Exists(profile.InstallPath) || !File.Exists(serverExe))
                return (false, $"Choose the folder containing {adapter.ExecutableName} before starting this profile.");
            if (profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase) && (profile.Port is < 1024 or > 65534))
                return (false, "Choose a port between 1024 and 65534 so Valheim's two-port range fits.");
            if (string.IsNullOrWhiteSpace(profile.GamePassword))
                return (false, "Set the game password in this profile before starting the server.");

            var instanceData = InstanceDirectory(profile.Id);
            var saveDirectory = SavePathResolver.Resolve(profile, state.DataDirectory);
            Directory.CreateDirectory(saveDirectory);
            Directory.CreateDirectory(instanceData);
            var logPath = state.GetServerLogPath(profile.Id);
            _logPaths[profile.Id] = logPath;
            _logs[profile.Id] = new ConsoleLogBuffer(logPath);
            var start = adapter.BuildStartInfo(profile, saveDirectory);

            try
            {
                var process = await StartConsoleBridgeAsync(profile.Id, instanceData, logPath, start);
                _processes[profile.Id] = process;
                _ = ObserveExitAsync(profile.Id, logPath, process);
                return (true, $"Started process {process.Process.Id} with a persistent console connection.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or PlatformNotSupportedException)
            {
                return (false, $"Could not start the server: {ex.Message}");
            }
        }
        finally { gate.Release(); }
    }

    public async Task<(bool Success, string Message)> StopAsync(Guid id)
    {
        var gate = _operations.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_processes.TryGetValue(id, out var process) || process.HasExited)
                return (true, "The server is already stopped.");
            var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
            if (profile is null || !adapters.TryGet(profile.GameId, out var adapter))
                return (false, "The server adapter is unavailable, so the process was left running.");
            try { return await adapter.StopAsync(process, profile); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
            {
                return (false, "Could not request a safe stop from this process. It was left running to protect the world save.");
            }
        }
        finally { gate.Release(); }
    }

    public async Task<(bool Success, string Message)> ForceStopAsync(Guid id)
    {
        var gate = _operations.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_processes.TryGetValue(id, out var managed))
                return (true, "O servidor já está parado.");
            var process = managed.Process;
            try
            {
                process.Refresh();
                if (process.HasExited) return (true, "O servidor já está parado.");
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                { return (false, "O Windows não confirmou o encerramento forçado do processo."); }
                return process.HasExited
                    ? (true, "Processo encerrado à força. O progresso desde o último salvamento pode ter sido perdido.")
                    : (false, "O processo ainda está em execução após a solicitação de encerramento forçado.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return (false, $"Não foi possível encerrar o processo: {ex.Message}");
            }
        }
        finally { gate.Release(); }
    }

    public async Task<(bool Success, string Message)> RestartAsync(ServerProfile profile)
    {
        var stopped = await StopAsync(profile.Id);
        if (!stopped.Success) return stopped;
        var started = await StartAsync(profile);
        return started.Success ? (true, $"{stopped.Message} {started.Message}") : started;
    }

    public async Task<(bool Success, string Message)> SendCommandAsync(Guid id, string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return (false, "Enter a console command first.");
        if (command.Length > 1000 || command.Contains('\n') || command.Contains('\r') || command.Contains('\0'))
            return (false, "Commands must fit on one line and contain at most 1000 characters.");
        if (!_processes.TryGetValue(id, out var process) || process.HasExited)
            return (false, "Start the server before sending console commands.");
        try
        {
            await process.SendCommandAsync(command);
            return (true, "Command sent to the server console.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            return (false, "The server console is no longer accepting commands.");
        }
    }

    public long GetProfileStorageBytes(ServerProfile profile)
    {
        var now = Stopwatch.GetTimestamp();
        if (_storageUsageCache.TryGetValue(profile.Id, out var cached) && Stopwatch.GetElapsedTime(cached.SampledAt, now) < TimeSpan.FromSeconds(30))
            return cached.Bytes;

        long bytes = 0;
        var saveDirectory = Path.GetFullPath(SavePathResolver.Resolve(profile, state.DataDirectory));
        var instanceDirectory = Path.GetFullPath(InstanceDirectory(profile.Id));
        bytes = SaturatingAdd(bytes, DirectorySize(instanceDirectory));
        if (!IsPathWithin(instanceDirectory, saveDirectory))
        {
            var sharedInstallSaves = profile.UseInstallPathSaves;
            bytes = SaturatingAdd(bytes, sharedInstallSaves
                ? WorldSaveSize(saveDirectory, profile.WorldName)
                : DirectorySize(saveDirectory));
        }
        _storageUsageCache[profile.Id] = (now, bytes);
        return bytes;
    }

    private async Task<ConsoleBridgeManagedProcess> StartConsoleBridgeAsync(Guid profileId, string instanceDirectory, string logPath, ProcessStartInfo serverStart)
    {
        var pipeName = ConsolePipeName(profileId);
        var statusPath = Path.Combine(instanceDirectory, "console-bridge.json");
        var configPath = Path.Combine(instanceDirectory, $"console-bridge-start-{Guid.NewGuid():N}.json");
        var startupErrorPath = Path.Combine(instanceDirectory, $"console-bridge-error-{Guid.NewGuid():N}.txt");
        var launch = new ConsoleBridgeLaunchConfig(pipeName, logPath, statusPath, startupErrorPath, serverStart.FileName,
            serverStart.WorkingDirectory, serverStart.ArgumentList.ToArray());
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(launch, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException("The DSS process path could not be determined.");
        var bridgeStart = new ProcessStartInfo
        {
            FileName = hostPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(hostPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            bridgeStart.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("The DSS assembly path could not be determined."));
        bridgeStart.ArgumentList.Add("--console-bridge");
        bridgeStart.ArgumentList.Add(configPath);

        try
        {
            using var bridge = Process.Start(bridgeStart) ?? throw new InvalidOperationException("The persistent server console bridge could not start.");
            try
            {
                return await ConsoleBridgeManagedProcess.WaitForStartAsync(pipeName, bridge.Id, startupErrorPath, TimeSpan.FromSeconds(45));
            }
            catch
            {
                try { if (!bridge.HasExited) bridge.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                throw;
            }
        }
        finally
        {
            TryDelete(configPath);
            TryDelete(startupErrorPath);
            TryDelete(startupErrorPath + ".tmp");
        }
    }

    private void AppendLog(Guid id, string path, string stream, string? line)
    {
        if (line is null) return;
        var buffer = _logs.GetOrAdd(id, _ => new ConsoleLogBuffer(path));
        buffer.Append(stream, line, path);
    }

    private async Task ObserveExitAsync(Guid id, string logPath, IManagedServerProcess process)
    {
        try
        {
            await process.Process.WaitForExitAsync();
            AppendLog(id, logPath, "manager", $"Server process exited with code {process.Process.ExitCode}.");
        }
        catch (InvalidOperationException) { }
    }

    private sealed class ConsoleLogBuffer
    {
        private const int MaxVisibleLines = 1000;
        private readonly object _gate = new();
        private readonly Queue<ConsoleLogEntry> _entries = new();
        private long _sequence;
        private long _filePosition;

        public ConsoleLogBuffer(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var recent = new Queue<string>();
                while (reader.ReadLine() is { } line)
                {
                    recent.Enqueue(line);
                    while (recent.Count > MaxVisibleLines) recent.Dequeue();
                }
                foreach (var line in recent) _entries.Enqueue(new(++_sequence, DateTimeOffset.Now, "history", line));
                _filePosition = stream.Position;
            }
            catch (IOException) { }
        }

        public void Append(string stream, string text, string path)
        {
            lock (_gate)
            {
                var timestamp = DateTimeOffset.Now;
                try { File.AppendAllText(path, $"[{timestamp:O}] {text}{Environment.NewLine}"); }
                catch (IOException) { }
                _entries.Enqueue(new(++_sequence, timestamp, stream, text));
                while (_entries.Count > MaxVisibleLines) _entries.Dequeue();
                try { _filePosition = new FileInfo(path).Length; } catch (IOException) { }
            }
        }

        public void RefreshFromFile(string path)
        {
            if (!File.Exists(path)) return;
            lock (_gate)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length < _filePosition) _filePosition = 0;
                    stream.Position = _filePosition;
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    while (reader.ReadLine() is { } line)
                    {
                        _entries.Enqueue(new(++_sequence, DateTimeOffset.Now, "server", line));
                        while (_entries.Count > MaxVisibleLines) _entries.Dequeue();
                    }
                    _filePosition = stream.Position;
                }
                catch (IOException) { }
            }
        }

        public (IReadOnlyList<ConsoleLogEntry> Entries, long Sequence) Read(long afterSequence, int count)
        {
            lock (_gate)
                return (_entries.Where(entry => entry.Sequence > afterSequence).TakeLast(count).ToArray(), _sequence);
        }
    }

    private string InstanceDirectory(Guid id) => Path.Combine(state.DataDirectory, "instances", id.ToString("N"));

    private static string ConsolePipeName(Guid id) => $"dss-console-{id:N}";

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }

    private static long SaturatingAdd(long left, long right) => right > long.MaxValue - left ? long.MaxValue : left + right;

    private static long DirectorySize(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string path = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    try { total = SaturatingAdd(total, new FileInfo(file).Length); } catch (IOException) { }
                }
                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    try { if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory); } catch (IOException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    private static long WorldSaveSize(string root, string worldName)
    {
        if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(worldName)) return 0;
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith(worldName + ".", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith(worldName + "_", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, worldName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { total = SaturatingAdd(total, new FileInfo(file).Length); } catch (IOException) { }
                }
                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    try { if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory); } catch (IOException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    private static bool IsPathWithin(string parent, string candidate)
    {
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<int, int> GetUdpPortOwners()
    {
        var result = new Dictionary<int, int>();
        if (!OperatingSystem.IsWindows()) return result;
        var size = 0;
        var query = GetExtendedUdpTable(IntPtr.Zero, ref size, true, 2, 1, 0);
        if (query != 122 || size <= sizeof(uint)) return result;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            query = GetExtendedUdpTable(table, ref size, true, 2, 1, 0);
            if (query != 0) return result;
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<UdpOwnerRow>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<UdpOwnerRow>(IntPtr.Add(table, sizeof(uint) + i * rowSize));
                var networkPort = (ushort)(row.LocalPort & 0xffff);
                var port = ((networkPort & 0x00ff) << 8) | ((networkPort & 0xff00) >> 8);
                if (port > 0 && row.ProcessId > 0) result.TryAdd(port, checked((int)row.ProcessId));
            }
        }
        finally { Marshal.FreeHGlobal(table); }
        return result;
    }

    private sealed class RecoveredLegacyServerProcess(Process process) : IManagedServerProcess
    {
        public Process Process { get; } = process;
        public bool ConsoleConnected => false;
        public bool HasExited { get { try { Process.Refresh(); return Process.HasExited; } catch (InvalidOperationException) { return true; } } }
        public Task SendCommandAsync(string command) => throw new IOException("The original console pipes are unavailable after restarting the DSS.");
        public Task<bool> SendCtrlBreakAsync(TimeSpan timeout) => ManagedServerProcess.SendCtrlBreakToProcessAsync(Process, timeout);
    }

    private static (long ReadBytes, long WriteBytes) GetIoBytes(Process process)
    {
        if (OperatingSystem.IsWindows() && GetProcessIoCounters(process.Handle, out var counters))
            return (ToLong(counters.ReadTransferCount), ToLong(counters.WriteTransferCount));
        if (OperatingSystem.IsLinux())
        {
            try
            {
                long read = 0, written = 0;
                foreach (var line in File.ReadLines($"/proc/{process.Id}/io"))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length != 2 || !long.TryParse(parts[1].Trim(), out var value)) continue;
                    if (parts[0] == "rchar") read = value;
                    else if (parts[0] == "wchar") written = value;
                }
                return (read, written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (0, 0);
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpOwnerRow { public uint LocalAddress, LocalPort, ProcessId; }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);
}
