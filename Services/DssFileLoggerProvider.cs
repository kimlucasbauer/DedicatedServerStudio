using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DedicatedServerStudio.Services;

/// <summary>Writes DSS application diagnostics to daily files without mixing in game-server console output.</summary>
public sealed class DssFileLoggerProvider(string logsDirectory) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, DssLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _activeLogDate;
    private bool _disposed;

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new DssLogger(this, name));

    internal bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;

    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var timestamp = DateTimeOffset.Now;
                var date = timestamp.ToString("yyyy-MM-dd");
                if (!string.Equals(_activeLogDate, date, StringComparison.Ordinal))
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(logsDirectory);
                    var path = Path.Combine(logsDirectory, $"dss-{date}.log");
                    _writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true
                    };
                    _activeLogDate = date;
                }

                var eventLabel = eventId.Id == 0 ? string.Empty : $" EventId={eventId.Id}";
                if (!string.IsNullOrWhiteSpace(eventId.Name)) eventLabel += $" ({eventId.Name})";
                _writer!.WriteLine($"{timestamp:O} [{level}] {category}{eventLabel}: {message}");
                if (exception is not null) _writer.WriteLine(exception);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class DssLogger(DssFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null) return;
            provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
