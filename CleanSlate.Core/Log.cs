using System.Text;

namespace CleanSlate.Core;

public interface ILogSink
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class NullLog : ILogSink
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}

public sealed record LogEntry(DateTime Time, string Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>Журнал: пишет в файл %LOCALAPPDATA%\CleanSlate\logs и сообщает UI о новых записях.</summary>
public sealed class AppLog : ILogSink
{
    public static AppLog Instance { get; } = new();

    readonly object _gate = new();
    public event Action<LogEntry>? EntryAdded;

    public string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSlate", "logs");

    public string CurrentFile => Path.Combine(LogDirectory, $"cleanslate-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    void Write(string level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentFile,
                    $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { /* сбой журнала не должен ронять приложение */ }
        }
        try { EntryAdded?.Invoke(entry); } catch { }
    }
}
