using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DedicatedServerStudio.Services;

/// <summary>Starts a hidden console process group with redirected pipes so logs, input, and Ctrl+Break are available to the web host.</summary>
public sealed class ManagedServerProcess : IManagedServerProcess
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint CtrlBreakEvent = 1;
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private readonly StreamWriter _input;
    private readonly Process _process;

    public Process Process => _process;
    public bool ConsoleConnected => true;
    public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }

    private ManagedServerProcess(Process process, StreamWriter input)
    {
        _process = process;
        _input = input;
    }

    public static ManagedServerProcess Start(ProcessStartInfo start, Action<string, string?> onOutput)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Valheim server adapter currently requires Windows.");

        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        IntPtr stdoutRead = IntPtr.Zero, stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero, stderrWrite = IntPtr.Zero;
        IntPtr stdinRead = IntPtr.Zero, stdinWrite = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero, threadHandle = IntPtr.Zero;
        SafeFileHandle? safeStdout = null, safeStderr = null, safeStdin = null;
        StreamReader? outputReader = null, errorReader = null;
        StreamWriter? inputWriter = null;
        var processCreated = false;
        try
        {
            CreatePipePair(out stdoutRead, out stdoutWrite, ref security);
            CreatePipePair(out stderrRead, out stderrWrite, ref security);
            CreatePipePair(out stdinRead, out stdinWrite, ref security);
            if (!SetHandleInformation(stdoutRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(stderrRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(stdinWrite, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect the server console pipes.");

            // CreatePipe returns synchronous handles. Wrap them as synchronous FileStreams;
            // FileStream(isAsync: true) rejects these handles on Windows after CreateProcess
            // has already launched the game, leaving an unmanaged server behind.
            safeStdout = new SafeFileHandle(stdoutRead, ownsHandle: true); stdoutRead = IntPtr.Zero;
            safeStderr = new SafeFileHandle(stderrRead, ownsHandle: true); stderrRead = IntPtr.Zero;
            safeStdin = new SafeFileHandle(stdinWrite, ownsHandle: true); stdinWrite = IntPtr.Zero;
            outputReader = new StreamReader(new FileStream(safeStdout, FileAccess.Read, 4096, isAsync: false), new UTF8Encoding(false, false));
            errorReader = new StreamReader(new FileStream(safeStderr, FileAccess.Read, 4096, isAsync: false), new UTF8Encoding(false, false));
            inputWriter = new StreamWriter(new FileStream(safeStdin, FileAccess.Write, 4096, isAsync: false), new UTF8Encoding(false, false)) { AutoFlush = true };

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseShowWindow | StartfUseStdHandles,
                ShowWindow = 0,
                StandardInput = stdinRead,
                StandardOutput = stdoutWrite,
                StandardError = stderrWrite
            };
            var commandLine = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(QuoteWindowsArgument)));
            if (!CreateProcess(start.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true, CreateNewProcessGroup,
                IntPtr.Zero, string.IsNullOrWhiteSpace(start.WorkingDirectory) ? null : start.WorkingDirectory,
                ref startup, out var info))
            {
                var errorCode = Marshal.GetLastWin32Error();
                throw new Win32Exception(errorCode, $"Windows could not create the server process (error {errorCode}).");
            }

            processHandle = info.Process;
            threadHandle = info.Thread;
            processCreated = true;
            CloseIfOpen(ref stdoutWrite);
            CloseIfOpen(ref stderrWrite);
            CloseIfOpen(ref stdinRead);

            var process = Process.GetProcessById(unchecked((int)info.ProcessId));
            var managed = new ManagedServerProcess(process, inputWriter!);
            _ = PumpLinesAsync(outputReader!, "stdout", onOutput);
            _ = PumpLinesAsync(errorReader!, "stderr", onOutput);
            return managed;
        }
        catch
        {
            if (processCreated && processHandle != IntPtr.Zero)
                TerminateProcess(processHandle, 1);
            outputReader?.Dispose();
            errorReader?.Dispose();
            inputWriter?.Dispose();
            safeStdout?.Dispose(); safeStderr?.Dispose(); safeStdin?.Dispose();
            throw;
        }
        finally
        {
            CloseIfOpen(ref stdoutRead); CloseIfOpen(ref stdoutWrite);
            CloseIfOpen(ref stderrRead); CloseIfOpen(ref stderrWrite);
            CloseIfOpen(ref stdinRead); CloseIfOpen(ref stdinWrite);
            CloseIfOpen(ref processHandle); CloseIfOpen(ref threadHandle);
        }
    }

    public async Task SendCommandAsync(string command)
    {
        await _input.WriteLineAsync(command);
        await _input.FlushAsync();
    }

    public async Task<bool> SendCtrlBreakAsync(TimeSpan timeout)
        => await SendCtrlBreakToProcessAsync(_process, timeout);

    public static async Task<bool> SendCtrlBreakToProcessAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited) return true;
        var attachedConsole = false;
        var wasAttachedToConsole = GetConsoleWindow() != IntPtr.Zero;
        var ignoreCtrlEvents = false;
        try
        {
            // After a DSS restart, the new panel can be attached to a different
            // terminal from the server. Detach first so Ctrl+Break targets the
            // console that owns this process instead of the new panel's console.
            if (wasAttachedToConsole && !FreeConsole()) return false;
            attachedConsole = AttachConsole(unchecked((uint)process.Id));
            if (!attachedConsole) return false;
            ignoreCtrlEvents = SetConsoleCtrlHandler(IntPtr.Zero, true);
            if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, unchecked((uint)process.Id))) return false;
            using var cancellation = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch (OperationCanceledException) { return false; }
            return process.HasExited;
        }
        finally
        {
            if (ignoreCtrlEvents) SetConsoleCtrlHandler(IntPtr.Zero, false);
            if (attachedConsole) FreeConsole();
            if (wasAttachedToConsole) AttachConsole(AttachParentProcess);
        }
    }

    private static async Task PumpLinesAsync(StreamReader reader, string stream, Action<string, string?> onOutput)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) onOutput(stream, line);
        }
        catch (IOException) { }
        finally { reader.Dispose(); }
    }

    private static void CreatePipePair(out IntPtr read, out IntPtr write, ref SecurityAttributes security)
    {
        if (!CreatePipe(out read, out write, ref security, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a server console pipe.");
    }

    private static void CloseIfOpen(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2Length;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint eventType, uint processGroupId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
