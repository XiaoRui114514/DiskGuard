using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskGuard.Core.Config;
using DiskGuard.Core.Engine;
using DiskGuard.Core.Interop;
using DiskGuard.Core.Localization;
using DiskGuard.Core.Util;

namespace DiskGuard.Core.Logging;

/// <summary>十分钟内存环形记录；只有开发者模式开启时写盘。序列化、清理和写盘都在后台。</summary>
public sealed class DeveloperRecorder : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private const int MaxRecords = 2400;
    private const long MaxStorageBytes = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly object _sync = new();
    private readonly object _fileSync = new();
    private readonly Queue<DeveloperLogEntry> _recent = new();
    private readonly Dictionary<string, (long First, long Last, int Count)> _fileVersions = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _writer;
    private readonly AppLogger _logger;
    private readonly TimeProvider _clock;
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAtUtc;
    private readonly string _version;
    private volatile bool _stopping;
    private bool _enabled;
    private long _sequence;
    private string _settingsJson = "{}";
    private string? _writtenContext;
    private long _lastCpuAt;
    private TimeSpan _lastCpuTime;
    private NativeMethods.IO_COUNTERS _lastIo;
    private long _uiHeartbeatAt;
    private double _uiDelayMs;
    private double _uiRefreshMs;
    private bool _windowVisible;
    private string? _lastError;

    public string DirectoryPath { get; }
    public bool Enabled { get { lock (_sync) return _enabled; } }
    public string? LastError { get { lock (_sync) return _lastError; } }

    public DeveloperRecorder(AppLogger logger, string directory, string version, AppSettings settings,
        TimeProvider? clock = null)
    {
        _logger = logger;
        DirectoryPath = directory;
        _version = version;
        _clock = clock ?? TimeProvider.System;
        _startedAtUtc = _clock.GetUtcNow();
        _logger.EntryWritten += RecordEvent;
        Configure(settings);
        _writer = new Thread(WriterLoop)
        {
            IsBackground = true, Name = "DiskGuard-DeveloperLog", Priority = ThreadPriority.BelowNormal
        };
        _writer.Start();
    }

    public void Configure(AppSettings settings)
    {
        // 设置很小，复制后不再持有界面正在修改的 AppSettings 实例。
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        lock (_sync)
        {
            _enabled = settings.DeveloperMode;
            if (json != _settingsJson)
            {
                _settingsJson = json;
                AddLocked("settings", JsonSerializer.Deserialize<JsonElement>(json),
                    Loc.T(_enabled ? LK.DeveloperRecordingOn : LK.DeveloperRecordingOff));
            }
        }
        _wake.Set();
    }

    public void RecordUiHeartbeat(double delayMs, bool visible)
    {
        lock (_sync)
        {
            _uiDelayMs = Math.Max(0, delayMs);
            _uiHeartbeatAt = Stopwatch.GetTimestamp();
            _windowVisible = visible;
        }
    }

    public void RecordUiRefresh(double durationMs)
    {
        lock (_sync) _uiRefreshMs = Math.Max(0, durationMs);
    }

    public void RecordSnapshot(EngineSnapshot snapshot)
    {
        if (_stopping) return;
        // DiskSampler 复用并修改 DiskStatus，必须当场复制数值，历史不能跟着下一秒变化。
        var disks = snapshot.Disks.Where(d => d.DiskNumber >= 0).Select(d => new
        {
            d.DiskNumber, d.InstanceName, d.Drives,
            d.BusyPercent, d.QueueLength, d.ReadBytesPerSec, d.WriteBytesPerSec,
            LatencyMs = d.LatencyValid ? (double?)d.LatencyMs : null
        }).ToArray();
        var processes = snapshot.Rows.Take(25).Select(p => new
        {
            p.Pid, p.Name, p.Tag, p.ReadBytesPerSec, p.WriteBytesPerSec,
            p.CurrentBytesPerSec, p.AverageBytesPerSec, p.IoCount, p.CurrentIoCount,
            p.AverageIoCount, p.OccupancyPercent, p.AverageOccupancyPercent, p.ServiceTimeMs
        }).ToArray();

        object runtime = ReadRuntime();
        lock (_sync)
        {
            AddLocked("sample", new
            {
                SampledAt = snapshot.Timestamp,
                snapshot.SampleIntervalMs, snapshot.SampleDurationMs,
                snapshot.DiskNumber, snapshot.SelectedDiskAvailable,
                Disks = disks,
                snapshot.IoMode, snapshot.IoPrecise,
                ProcessScope = snapshot.IoPrecise ? "selectedPhysicalDisk" : "allProcessIoApproximation",
                snapshot.IoEventCount, snapshot.IoHealthError, snapshot.ForegroundPid,
                Processes = processes,
                Guard = new
                {
                    snapshot.Paused, snapshot.StateKind, snapshot.StateText,
                    snapshot.ActivePid, snapshot.ActiveName, snapshot.ActiveLevel, snapshot.ActiveSince,
                    snapshot.ActiveCapBytesPerSec, snapshot.ActiveCapIops,
                    snapshot.ActiveOccupancyPercent, snapshot.ActiveRateBytesPerSec,
                    snapshot.TriggerPercent, snapshot.RecoverPercent,
                    snapshot.TriggerOccupancyPercent, snapshot.TargetOccupancyPercent,
                    DiskHardLimitPercent = 98
                },
                System = new
                {
                    snapshot.SystemOccupancyPercent, snapshot.SystemRateBytesPerSec,
                    snapshot.SystemIoCount, snapshot.SystemCpuPercent, snapshot.StartupAgeSeconds
                },
                App = runtime,
                Ui = new
                {
                    HeartbeatAgeMs = _uiHeartbeatAt == 0 ? (double?)null : Stopwatch.GetElapsedTime(_uiHeartbeatAt).TotalMilliseconds,
                    HeartbeatDelayMs = _uiDelayMs, RefreshDurationMs = _uiRefreshMs, WindowVisible = _windowVisible
                }
            }, snapshot.StateText,
                string.Join(" · ", disks.Select(d => $"{d.DiskNumber}: {d.BusyPercent:0.#}%")),
                disks.Where(d => d.LatencyMs.HasValue).Select(d => d.LatencyMs).DefaultIfEmpty().Max());
        }
    }

    private object ReadRuntime()
    {
        try
        {
            _self.Refresh();
            long now = Stopwatch.GetTimestamp();
            TimeSpan cpu = _self.TotalProcessorTime;
            bool ioValid = NativeMethods.GetProcessIoCounters(NativeMethods.GetCurrentProcess(), out var io);
            double elapsed = _lastCpuAt == 0 ? 0 : Stopwatch.GetElapsedTime(_lastCpuAt, now).TotalSeconds;
            var runtime = new
            {
                Pid = Environment.ProcessId,
                CpuPercent = elapsed <= 0 ? (double?)null : Math.Max(0, (cpu - _lastCpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100),
                WorkingSetBytes = _self.WorkingSet64, PrivateMemoryBytes = _self.PrivateMemorySize64,
                ManagedMemoryBytes = GC.GetTotalMemory(false),
                ReadBytesPerSec = !ioValid || elapsed <= 0 ? (double?)null : Math.Max(0, ((double)io.ReadTransferCount - _lastIo.ReadTransferCount) / elapsed),
                WriteBytesPerSec = !ioValid || elapsed <= 0 ? (double?)null : Math.Max(0, ((double)io.WriteTransferCount - _lastIo.WriteTransferCount) / elapsed),
                Gen0Collections = GC.CollectionCount(0), Gen1Collections = GC.CollectionCount(1), Gen2Collections = GC.CollectionCount(2)
            };
            _lastCpuAt = now;
            _lastCpuTime = cpu;
            _lastIo = io;
            return runtime;
        }
        catch (Exception ex) { return new { Error = ex.Message }; }
    }

    private void RecordEvent(LogEntry entry)
    {
        if (_stopping) return;
        lock (_sync)
            AddLocked("event", new { entry.Timestamp, Level = entry.Level.ToString(), Message = entry.Message[..Math.Min(entry.Message.Length, 2048)] },
                entry.Message[..Math.Min(entry.Message.Length, 2048)]);
    }

    private void AddLocked(string kind, object data, string summary, string disks = "", double? latency = null)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        PruneLocked(now);
        _recent.Enqueue(new DeveloperLogEntry(++_sequence, now, _sessionId, kind, data, summary, disks, latency));
        while (_recent.Count > MaxRecords) _recent.Dequeue();
    }

    private void PruneLocked(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - Retention;
        while (_recent.Count > 0 && _recent.Peek().TimestampUtc < cutoff) _recent.Dequeue();
        // 校时/用户改时钟之后不能让“未来”记录永久占据缓存。
        if (_recent.Any(r => r.TimestampUtc > now))
        {
            var valid = _recent.Where(r => r.TimestampUtc >= cutoff && r.TimestampUtc <= now).ToArray();
            _recent.Clear();
            foreach (var record in valid) _recent.Enqueue(record);
        }
    }

    public IReadOnlyList<DeveloperLogEntry> GetRecent()
    {
        lock (_sync)
        {
            PruneLocked(_clock.GetUtcNow());
            return _enabled ? _recent.Reverse().ToArray() : Array.Empty<DeveloperLogEntry>();
        }
    }

    public Task FlushAsync() => Task.Run(Flush);

    private void WriterLoop()
    {
        do
        {
            Flush();
            _wake.WaitOne(TimeSpan.FromSeconds(5));
        } while (!_stopping);
        Flush();
        _self.Dispose();
    }

    private void Flush()
    {
        lock (_fileSync)
        {
            try
            {
                FlushCore();
                lock (_sync) _lastError = null;
            }
            catch (Exception ex)
            {
                bool report;
                lock (_sync) { report = _lastError == null; _lastError = ex.Message; }
                if (report)
                {
                    try { _logger.Warn(Loc.F(LK.DeveloperWriteFailedFormat, ex.Message)); }
                    catch { } // 退出时界面订阅者已关闭，诊断写入失败也不能终止整个进程。
                }
            }
        }
    }

    private void FlushCore()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset cutoff = now - Retention;
        DeveloperLogEntry[] records;
        bool enabled;
        string settingsJson;
        lock (_sync)
        {
            PruneLocked(now);
            records = _recent.ToArray();
            enabled = _enabled;
            settingsJson = _settingsJson;
        }

        if (!enabled && !Directory.Exists(DirectoryPath)) return;
        Directory.CreateDirectory(DirectoryPath);
        // 未完成的原子写入（如上次掉电）不能积攒；只清理本记录器生成的文件。
        foreach (string temporary in Directory.EnumerateFiles(DirectoryPath, "*.jsonl.tmp"))
            if (TryParseMinute(Path.GetFileName(temporary)[..^10], out _)) File.Delete(temporary);
        string contextPath = Path.Combine(DirectoryPath, "session.json");
        if (File.Exists(contextPath + ".tmp")) File.Delete(contextPath + ".tmp");

        var groups = enabled
            ? records.GroupBy(r => r.TimestampUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => g.ToArray())
            : new Dictionary<string, DeveloperLogEntry[]>();

        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.jsonl"))
        {
            string key = Path.GetFileNameWithoutExtension(path);
            if (!TryParseMinute(key, out var minute)) continue;
            if (minute.AddMinutes(1) <= cutoff || minute > now)
            {
                File.Delete(path);
                _fileVersions.Remove(key);
            }
            else if (!groups.ContainsKey(key) && (minute < cutoff || minute.AddMinutes(1) > now))
            {
                // 关闭开发者模式或刚重启时，边界分钟也逐行裁剪，不能多留一分钟。
                var kept = File.ReadLines(path).Where(line => IsWithinWindow(line, cutoff, now)).ToArray();
                if (kept.Length == 0) File.Delete(path);
                else WriteAtomic(path, kept);
                _fileVersions.Remove(key);
            }
        }

        if (!enabled)
        {
            _writtenContext = null;
            _fileVersions.Clear();
            if (!Directory.EnumerateFiles(DirectoryPath, "*.jsonl").Any() && File.Exists(contextPath)) File.Delete(contextPath);
            return;
        }

        if (_writtenContext != settingsJson)
        {
            WriteAtomic(contextPath, new[] { JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, AppVersion = _version, SessionId = _sessionId,
                StartedAtUtc = _startedAtUtc, RetentionSeconds = Retention.TotalSeconds,
                FlushIntervalSeconds = 5, MaxRecords, MaxStorageBytes,
                OS = Environment.OSVersion.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount, TimeZone = TimeZoneInfo.Local.Id,
                ProcessIoNote = "Precise process IO is for the selected physical disk; fallback includes file/device/network IO and is approximate.",
                Settings = JsonSerializer.Deserialize<JsonElement>(settingsJson)
            }, JsonOptions) });
            _writtenContext = settingsJson;
        }

        foreach (var (key, entries) in groups)
        {
            var revision = (entries[0].Sequence, entries[^1].Sequence, entries.Length);
            if (_fileVersions.TryGetValue(key, out var previous) && previous == revision) continue;
            WriteAtomic(Path.Combine(DirectoryPath, key + ".jsonl"), entries.Select(e => e.ToJson()));
            _fileVersions[key] = revision;
        }

        // 独立容量兜底，异常日志风暴也不能无限增长。正常十分钟记录远小于此值。
        var files = new DirectoryInfo(DirectoryPath).GetFiles("*.jsonl")
            .Where(f => TryParseMinute(Path.GetFileNameWithoutExtension(f.Name), out _)).OrderBy(f => f.Name).ToArray();
        long bytes = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (bytes <= MaxStorageBytes) break;
            bytes -= file.Length;
            string key = Path.GetFileNameWithoutExtension(file.Name);
            file.Delete();
            _fileVersions.Remove(key);
            lock (_sync)
            {
                var kept = _recent.Where(r => r.TimestampUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) != key).ToArray();
                _recent.Clear();
                foreach (var record in kept) _recent.Enqueue(record);
            }
        }
    }

    private static bool IsWithinWindow(string line, DateTimeOffset cutoff, DateTimeOffset now)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            DateTimeOffset time = json.RootElement.GetProperty("timestampUtc").GetDateTimeOffset();
            return time >= cutoff && time <= now;
        }
        catch { return false; } // 掉电造成的残缺行不妨碍读取其余数据。
    }

    private static bool TryParseMinute(string key, out DateTimeOffset minute) => DateTimeOffset.TryParseExact(
        key, "yyyyMMdd-HHmm", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out minute);

    private static void WriteAtomic(string path, IEnumerable<string> lines)
    {
        string temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _logger.EntryWritten -= RecordEvent;
        _wake.Set();
        // 磁盘卡住时不让退出界面等候无限久；后台线程会尽量完成最后一批写入。
        if (_writer.Join(TimeSpan.FromSeconds(1))) _wake.Dispose();
    }
}

public sealed class DeveloperLogEntry
{
    [JsonIgnore] public long Sequence { get; }
    public DateTimeOffset TimestampUtc { get; }
    public string SessionId { get; }
    public string Kind { get; }
    public object Data { get; }
    [JsonIgnore] public string Summary { get; }
    [JsonIgnore] public string DiskSummary { get; }
    [JsonIgnore] public double? LatencyMs { get; }
    [JsonIgnore] public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    [JsonIgnore] public string KindText => Loc.T(Kind == "sample" ? LK.DeveloperSample : LK.DeveloperEvent);
    [JsonIgnore] public string LatencyText => LatencyMs.HasValue ? $"{LatencyMs:0.#} ms" : "—";
    private string? _json;

    internal DeveloperLogEntry(long sequence, DateTimeOffset timestamp, string sessionId, string kind,
        object data, string summary, string disks, double? latency)
    {
        Sequence = sequence; TimestampUtc = timestamp; SessionId = sessionId; Kind = kind;
        Data = data; Summary = summary; DiskSummary = disks; LatencyMs = latency;
    }

    public string ToJson(bool indented = false) => indented
        ? JsonSerializer.Serialize(this, new JsonSerializerOptions(DeveloperRecorder.JsonOptions) { WriteIndented = true })
        : _json ??= JsonSerializer.Serialize(this, DeveloperRecorder.JsonOptions);
}
