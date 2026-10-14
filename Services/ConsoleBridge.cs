using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed record ConsoleBridgeLaunchConfig(string PipeName, string LogPath, string StatusPath, string StartupErrorPath,
    string Executable, string WorkingDirectory, string[] Arguments);
public sealed record ConsoleBridgeStatus(int BridgeProcessId, int ServerProcessId, DateTimeOffset StartedAt, string? LogPath = null);
internal sealed record ConsoleBridgeRequest(string Type, string? Command = null, int TimeoutSeconds = 30);
internal sealed record ConsoleBridgeResponse(bool Success, string Message, int ProcessId = 0);

public sealed class ConsoleBridgeManagedProcess : IManagedServerProcess
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly int _bridgeProcessId;

    private ConsoleBridgeManagedProcess(string pipeName, int bridgeProcessId, Process serverProcess)
    {
        _pipeName = pipeName;
        _bridgeProcessId = bridgeProcessId;
        Process = serverProcess;
    }

    public Process Process { get; }
    public bool ConsoleConnected => true;
    public bool HasExited
    {
        get
        {
            try { Process.Refresh(); return Process.HasExited || !IsBridgeAlive(_bridgeProcessId); }
            catch (InvalidOperationException) { return true; }
        }
    }

    public static async Task<ConsoleBridgeManagedProcess> WaitForStartAsync(string pipeName, int bridgeProcessId,
        string startupErrorPath, TimeSpan timeout)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            var startupError = ReadStartupError(startupErrorPath);
            if (!string.IsNullOrWhiteSpace(startupError)) throw new InvalidOperationException(startupError);
            if (!IsBridgeAlive(bridgeProcessId))
                throw new InvalidOperationException(ReadStartupError(startupErrorPath) ??
                    "The persistent server console bridge exited before becoming available.");

            ConsoleBridgeResponse? response = null;
            try { response = await SendRequestAsync(pipeName, new ConsoleBridgeRequest("status"), TimeSpan.FromMilliseconds(600)); }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
            if (response is { Success: true, ProcessId: > 0 })
                return new ConsoleBridgeManagedProcess(pipeName, bridgeProcessId, System.Diagnostics.Process.GetProcessById(response.ProcessId));
            await Task.Delay(200);
        }
        throw new InvalidOperationException(ReadStartupError(startupErrorPath) ??
            "The persistent server console bridge did not become available within 45 seconds.");
    }

    private static string? ReadStartupError(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var message = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static async Task<ConsoleBridgeManagedProcess?> TryRecoverAsync(string pipeName, ConsoleBridgeStatus status)
    {
        if (!IsBridgeAlive(status.BridgeProcessId)) return null;
        try
        {
            var response = await SendRequestAsync(pipeName, new ConsoleBridgeRequest("status"), TimeSpan.FromMilliseconds(700));
            if (response is not { Success: true, ProcessId: > 0 } || response.ProcessId != status.ServerProcessId) return null;
            return new ConsoleBridgeManagedProcess(pipeName, status.BridgeProcessId, System.Diagnostics.Process.GetProcessById(response.ProcessId));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async Task SendCommandAsync(string command)
    {
        var response = await SendRequestAsync(_pipeName, new ConsoleBridgeRequest("command", command), TimeSpan.FromSeconds(3));
        if (response is not { Success: true }) throw new IOException(response?.Message ?? "The persistent console bridge is unavailable.");
    }

    public async Task<bool> SendCtrlBreakAsync(TimeSpan timeout)
    {
        var seconds = (int)Math.Clamp(Math.Ceiling(timeout.TotalSeconds), 1, 60);
        var response = await SendRequestAsync(_pipeName, new ConsoleBridgeRequest("ctrl-break", TimeoutSeconds: seconds), timeout + TimeSpan.FromSeconds(3));
        return response?.Success == true;
    }

    private static bool IsBridgeAlive(int processId)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(processId); return !process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static async Task<ConsoleBridgeResponse?> SendRequestAsync(string pipeName, ConsoleBridgeRequest request, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellation.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), cancellation.Token);
        var line = await reader.ReadLineAsync(cancellation.Token);
        return line is null ? null : JsonSerializer.Deserialize<ConsoleBridgeResponse>(line, JsonOptions);
    }
}

public static class ConsoleBridgeHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string configPath)
    {
        ConsoleBridgeLaunchConfig? config = null;
        ManagedServerProcess? process = null;
        try
        {
            config = JsonSerializer.Deserialize<ConsoleBridgeLaunchConfig>(await File.ReadAllTextAsync(configPath), JsonOptions)
                ?? throw new InvalidDataException("The console bridge configuration is empty.");
            File.Delete(configPath);
            var start = new ProcessStartInfo
            {
                FileName = config.Executable,
                WorkingDirectory = config.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in config.Arguments) start.ArgumentList.Add(argument);
            process = await StartServerProcessWithRetryAsync(start,
                (_, line) => { if (line is not null) AppendLog(config.LogPath, line); }, config.LogPath);
            WriteStatus(config, process.Process.Id);
            AppendLog(config.LogPath, $"Persistent console bridge connected to server process {process.Process.Id}.");

            var exitTask = process.Process.WaitForExitAsync();
            while (!process.HasExited)
            {
                using var pipe = new NamedPipeServerStream(config.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                var connectTask = pipe.WaitForConnectionAsync();
                if (await Task.WhenAny(connectTask, exitTask) == exitTask) break;
                await connectTask;
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var requestLine = await reader.ReadLineAsync();
                ConsoleBridgeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<ConsoleBridgeRequest>(requestLine ?? "", JsonOptions);
                    response = request?.Type switch
                    {
                        "status" => new ConsoleBridgeResponse(!process.HasExited, process.HasExited ? "Server is no longer running." : "Console reconnected.", process.Process.Id),
                        "command" when !string.IsNullOrWhiteSpace(request.Command) => await SendCommandAsync(process, request.Command),
                        "ctrl-break" => await SendCtrlBreakAsync(process, request.TimeoutSeconds),
                        _ => new ConsoleBridgeResponse(false, "Unsupported console bridge request.", process.Process.Id)
                    };
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ObjectDisposedException)
                {
                    response = new ConsoleBridgeResponse(false, ex.Message, process.Process.Id);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            }
            await exitTask;
            AppendLog(config.LogPath, $"Server process exited with code {process.Process.ExitCode}.");
        }
        catch (Exception ex)
        {
            var message = ex is Win32Exception nativeError
                ? $"{ex.Message} (Windows error {nativeError.NativeErrorCode})"
                : ex.Message;
            if (process is { HasExited: false })
            {
                try
                {
                    if (!await process.SendCtrlBreakAsync(TimeSpan.FromSeconds(8)) && !process.HasExited)
                        process.Process.Kill(entireProcessTree: true);
                }
                catch (Exception stopError) when (stopError is InvalidOperationException or IOException or ObjectDisposedException or Win32Exception)
                {
                    try { if (!process.HasExited) process.Process.Kill(entireProcessTree: true); }
                    catch (Exception killError) when (killError is InvalidOperationException or Win32Exception or NotSupportedException) { }
                }
            }
            if (config is not null)
            {
                WriteStartupError(config.StartupErrorPath, message);
                AppendLog(config.LogPath, $"Console bridge error: {message}");
            }
        }
        finally
        {
            if (config is not null)
            {
                try { if (File.Exists(config.StatusPath)) File.Delete(config.StatusPath); } catch (IOException) { }
            }
        }
    }

    private static async Task<ManagedServerProcess> StartServerProcessWithRetryAsync(ProcessStartInfo start,
        Action<string, string?> onOutput, string logPath)
    {
        TimeSpan[] retryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
        for (var attempt = 0; ; attempt++)
        {
            try { return ManagedServerProcess.Start(start, onOutput); }
            catch (Win32Exception ex) when (IsTransientStartError(ex.NativeErrorCode) && attempt < retryDelays.Length)
            {
                var delay = retryDelays[attempt];
                AppendLog(logPath, $"Windows temporarily refused to start the server (error {ex.NativeErrorCode}); retrying in {delay.TotalSeconds:0.##}s ({attempt + 2}/{retryDelays.Length + 1}).");
                await Task.Delay(delay);
            }
        }
    }

    private static bool IsTransientStartError(int errorCode) => errorCode is 5 or 8 or 14 or 21 or 32 or 33 or 1450 or 1455;

    private static void WriteStartupError(string path, string message)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, message);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private static async Task<ConsoleBridgeResponse> SendCommandAsync(ManagedServerProcess process, string command)
    {
        if (command.Length > 1000 || command.Contains('\n') || command.Contains('\r') || command.Contains('\0'))
            return new ConsoleBridgeResponse(false, "Commands must fit on one line and contain at most 1000 characters.", process.Process.Id);
        await process.SendCommandAsync(command);
        return new ConsoleBridgeResponse(true, "Command sent to the server console.", process.Process.Id);
    }

    private static async Task<ConsoleBridgeResponse> SendCtrlBreakAsync(ManagedServerProcess process, int timeoutSeconds)
    {
        var success = await process.SendCtrlBreakAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)));
        return new ConsoleBridgeResponse(success, success ? "Server stopped cleanly." : "The server did not confirm a clean stop.", process.Process.Id);
    }

    private static void WriteStatus(ConsoleBridgeLaunchConfig config, int serverProcessId)
    {
        var status = new ConsoleBridgeStatus(Environment.ProcessId, serverProcessId, DateTimeOffset.UtcNow, config.LogPath);
        var temporaryPath = config.StatusPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(status, JsonOptions));
        File.Move(temporaryPath, config.StatusPath, overwrite: true);
    }

    private static void AppendLog(string path, string text)
    {
        try { lock (path) File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
