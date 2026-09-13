using System.Collections.Concurrent;
using DiskGuard.Core.Localization;

namespace DiskGuard.Core.Logging;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;

    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogLevel.Warn => Loc.T(LK.LogLevelWarn),
        LogLevel.Error => Loc.T(LK.LogLevelError),
        _ => Loc.T(LK.LogLevelInfo)
    };
}

public sealed class AppLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly List<LogEntry> _recent = new();
    private readonly BlockingCollection<LogEntry> _pending = new(new ConcurrentQueue<LogEntry>(), 4000);
    private Thread? _writer;
    private bool _writeFailureReported;
    private volatile bool _disposed;

    public event Action<LogEntry>? EntryWritten;

    public bool WriteToFile { get; set; } = true;

    public string LogDirectory { get; set; } = string.Empty;

    public IReadOnlyList<LogEntry> Recent
    {
        get { lock (_sync) return _recent.ToArray(); }
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    public void Clear()
    {
        lock (_sync) _recent.Clear();
    }

    private void Write(LogLevel level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message };
        lock (_sync)
        {
            _recent.Add(entry);
            if (_recent.Count > 500) _recent.RemoveRange(0, _recent.Count - 500);
        }

        EntryWritten?.Invoke(entry);

        if (!WriteToFile || string.IsNullOrEmpty(LogDirectory)) return;

        // 写盘交给后台线程：磁盘繁忙时同步写文件可能阻塞数秒，
        // 出现在界面线程上就会表现为"卡住、打不了字"。
        if (_disposed) return;
        try
        {
            EnsureWriter();
            _pending.TryAdd(entry);
        }
        catch
        {
            // 退出过程中队列已关闭：只保留界面里的记录
        }
    }

    private void EnsureWriter()
    {
        if (_writer != null) return;

        lock (_sync)
        {
            if (_writer != null) return;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "DiskGuard-LogWriter" };
            _writer.Start();
        }
    }

    private void WriterLoop()
    {
        foreach (var entry in _pending.GetConsumingEnumerable())
        {
            if (!WriteToFile || string.IsNullOrEmpty(LogDirectory)) continue;

            try
            {
                Directory.CreateDirectory(LogDirectory);
                string file = Path.Combine(LogDirectory, entry.Timestamp.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(file, $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} [{entry.LevelText}] {entry.Message}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                ReportWriteFailure(ex);
            }
        }
    }

    /// <summary>日志写盘失败时在界面上提示一次，避免"看起来在运行、其实什么都没记"。</summary>
    private void ReportWriteFailure(Exception ex)
    {
        lock (_sync)
        {
            if (_writeFailureReported) return;
            _writeFailureReported = true;
        }

        var entry = new LogEntry
        {
            Level = LogLevel.Warn,
            Message = Loc.F(LK.LogLogFileWriteFailedFormat, ex.Message)
        };

        lock (_sync)
        {
            _recent.Add(entry);
            if (_recent.Count > 500) _recent.RemoveRange(0, _recent.Count - 500);
        }

        EntryWritten?.Invoke(entry);
    }

    /// <summary>退出前调用，尽量把队列里剩余日志写完。</summary>
    public void Dispose()
    {
        _disposed = true;
        try { _pending.CompleteAdding(); } catch { }
        try { _writer?.Join(TimeSpan.FromSeconds(1)); } catch { }
        try { _pending.Dispose(); } catch { }
    }
}
