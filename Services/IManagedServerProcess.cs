using System.Diagnostics;

namespace DedicatedServerStudio.Services;

public interface IManagedServerProcess
{
    Process Process { get; }
    bool HasExited { get; }
    bool ConsoleConnected { get; }
    Task SendCommandAsync(string command);
    Task<bool> SendCtrlBreakAsync(TimeSpan timeout);
}
