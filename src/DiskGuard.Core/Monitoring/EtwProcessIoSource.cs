using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace DiskGuard.Core.Monitoring;

/// <summary>
/// 使用 ETW 内核 DiskIO 事件，精确统计每个进程对指定磁盘的真实读写字节数
/// （不含网络 IO，可区分磁盘编号）。
/// </summary>
public sealed class EtwProcessIoSource : IProcessIoSource
{
    /// <summary>Microsoft-Windows-Kernel-Disk 清单提供程序 GUID（可在私有会话启用，不占用系统日志器槽位）。</summary>
    public static readonly Guid KernelDiskProviderGuid = new("C7BDE69A-E1E0-4177-B6EF-283AD1525271");

    private const int DiskReadEventId = 10;
    private const int DiskWriteEventId = 11;

    private sealed class Counter
    {
        public long Read;
        public long Write;
        public long ServiceMicroseconds;
        public long IoCount;
        public long IdleSnapshots;
    }

    private sealed class Recent
    {
        public readonly Queue<(double Read, double Write, double Iops, double ServiceMs)> Window = new();
    }

    private readonly ConcurrentDictionary<int, Counter> _io = new();
    private readonly ConcurrentDictionary<int, Recent> _recent = new();
    private readonly TraceEventSession _session;
    private readonly Thread _thread;
    private readonly int _diskNumber;
    private volatile string _error = string.Empty;
    private long _eventCount;
    private long _droppedDisk;
    private long _droppedPid;
    private long _droppedSize;

    /// <summary>诊断用：最近一次 Snapshot 返回的记录数。</summary>
    public int LastSnapshotItems { get; private set; }

    /// <summary>诊断用：因磁盘号/进程号/长度被丢弃的事件数。</summary>
    public long DroppedDisk => Interlocked.Read(ref _droppedDisk);
    public long DroppedPid => Interlocked.Read(ref _droppedPid);
    public long DroppedSize => Interlocked.Read(ref _droppedSize);

    /// <param name="usePrivateSession">
    /// true = 私有会话 + 内核清单提供程序（推荐，不占用 NT Kernel Logger 槽位）；
    /// false = 传统系统日志器方式（EnableKernelProvider）。
    /// </param>
    public EtwProcessIoSource(int diskNumber, bool usePrivateSession = true)
    {
        _diskNumber = diskNumber;
        _session = new TraceEventSession($"DiskGuardKernel_{Environment.ProcessId}_{(usePrivateSession ? "p" : "s")}")
        {
            StopOnDispose = true
        };

        _usePrivateSession = usePrivateSession;

        if (usePrivateSession)
            _session.EnableProvider(KernelDiskProviderGuid, TraceEventLevel.Verbose, ulong.MaxValue);
        else
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO);

        if (usePrivateSession)
        {
            _session.Source.Dynamic.All += OnManifestDiskEvent;
        }
        else
        {
            _session.Source.Kernel.DiskIORead += data => Accumulate(data.DiskNumber, data.ProcessID, data.TransferSize, ResolveServiceMs(data), true);
            _session.Source.Kernel.DiskIOWrite += data => Accumulate(data.DiskNumber, data.ProcessID, data.TransferSize, ResolveServiceMs(data), false);
        }

        _thread = new Thread(RunSession)
        {
            IsBackground = true,
            Name = "DiskGuard-Etw"
        };
        _thread.Start();
    }

    public string Mode => "ETW 精确统计（磁盘 " + _diskNumber + " 实际读写）";

    public bool IsPrecise => true;

    public bool ProvidesServiceTime => true;

    public string ShareMetricName => "磁盘时间占比";

    public string Error => _error;

    public string HealthError => _error;

    /// <summary>已接收的磁盘事件总数（用于判断私有会话是否真的收到了数据）。</summary>
    public long EventCount => Interlocked.Read(ref _eventCount);

    private readonly bool _usePrivateSession;

    /// <summary>解析 Microsoft-Windows-Kernel-Disk 清单事件（私有会话模式）。</summary>
    private void OnManifestDiskEvent(Microsoft.Diagnostics.Tracing.TraceEvent data)
    {
        try
        {
            if (data.ProviderGuid != KernelDiskProviderGuid) return;

            int id = (int)data.ID;
            bool isRead;
            if (id == DiskReadEventId) isRead = true;
            else if (id == DiskWriteEventId) isRead = false;
            else return;

            object? diskValue = data.PayloadByName("DiskNumber");
            int diskNumber = diskValue == null ? -1 : Convert.ToInt32(diskValue);
            if (diskNumber != _diskNumber) return;

            object? sizeValue = data.PayloadByName("TransferSize");
            long transferSize = sizeValue == null ? 0 : Convert.ToInt64(sizeValue);

            object? responseValue = data.PayloadByName("HighResResponseTime");
            double responseMs = responseValue == null ? 0 : Convert.ToInt64(responseValue) / 10000.0;   // 100ns 单位

            Accumulate(diskNumber, data.ProcessID, transferSize, responseMs, isRead);
        }
        catch
        {
            // 单条事件解析失败不影响整体统计
        }
    }

    private static double ResolveServiceMs(DiskIOTraceData data)
    {
        double serviceMs = data.DiskServiceTimeMSec;
        if (serviceMs <= 0) serviceMs = data.ElapsedTimeMSec;
        return serviceMs;
    }

    private void RunSession()
    {
        try
        {
            _session.Source.Process();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void Accumulate(int diskNumber, int pid, long transferSize, double serviceMs, bool isRead)
    {
        Interlocked.Increment(ref _eventCount);
        if (diskNumber != _diskNumber) { Interlocked.Increment(ref _droppedDisk); return; }
        if (pid <= 0) { Interlocked.Increment(ref _droppedPid); return; }

        if (transferSize <= 0) { Interlocked.Increment(ref _droppedSize); return; }

        var counter = _io.GetOrAdd(pid, _ => new Counter());
        if (isRead) Interlocked.Add(ref counter.Read, transferSize);
        else Interlocked.Add(ref counter.Write, transferSize);

        Interlocked.Increment(ref counter.IoCount);

        if (serviceMs > 0) Interlocked.Add(ref counter.ServiceMicroseconds, (long)Math.Round(serviceMs * 1000));
    }

    public IReadOnlyList<ProcessIoSample> Snapshot(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0.05) elapsedSeconds = 1.0;
        var list = new List<ProcessIoSample>(_io.Count);
        var deltas = new Dictionary<int, (double Read, double Write, double Iops, double ServiceMs)>(_io.Count);

        foreach (var pair in _io)
        {
            long read = Interlocked.Exchange(ref pair.Value.Read, 0);
            long write = Interlocked.Exchange(ref pair.Value.Write, 0);
            long serviceMicroseconds = Interlocked.Exchange(ref pair.Value.ServiceMicroseconds, 0);
            long ioCount = Interlocked.Exchange(ref pair.Value.IoCount, 0);

            if (read != 0 || write != 0 || ioCount != 0)
            {
                Interlocked.Exchange(ref pair.Value.IdleSnapshots, 0);
                deltas[pair.Key] = (read / elapsedSeconds, write / elapsedSeconds,
                    ioCount / elapsedSeconds, serviceMicroseconds / 1000.0);
            }
            else
            {
                Interlocked.Increment(ref pair.Value.IdleSnapshots);
            }
        }

        // 维护每个进程近 3 秒的窗口（没有活动的秒补 0），让界面与判定稳定，不受 ETW 分批到达影响
        foreach (var pair in _recent)
        {
            var value = deltas.TryGetValue(pair.Key, out var delta) ? delta : (0, 0, 0, 0);
            pair.Value.Window.Enqueue(value);
            while (pair.Value.Window.Count > 3) pair.Value.Window.Dequeue();
        }

        foreach (var pair in deltas)
        {
            if (_recent.ContainsKey(pair.Key)) continue;
            var recent = new Recent();
            recent.Window.Enqueue(pair.Value);
            _recent[pair.Key] = recent;
        }

        foreach (var pair in _recent)
        {
            var window = pair.Value.Window;
            if (window.Count == 0) continue;

            // 手写累加：这段每秒对每个进程都会跑一遍，LINQ 的委托/迭代器分配没必要
            double readSum = 0, writeSum = 0, iopsSum = 0, serviceSum = 0;
            foreach (var item in window)
            {
                readSum += item.Read;
                writeSum += item.Write;
                iopsSum += item.Iops;
                serviceSum += item.ServiceMs;
            }

            double samples = window.Count;
            double readAvg = readSum / samples;
            double writeAvg = writeSum / samples;
            double iopsAvg = iopsSum / samples;
            double serviceAvg = serviceSum / samples;

            if (readAvg < 512 && writeAvg < 512 && iopsAvg < 0.5)
            {
                // 连续多个快照都无活动才移除，避免列表闪烁
                if (_io.TryGetValue(pair.Key, out var counter) && Interlocked.Read(ref counter.IdleSnapshots) > 5)
                {
                    _recent.TryRemove(pair.Key, out _);
                    _io.TryRemove(pair.Key, out _);
                }
                continue;
            }

            list.Add(new ProcessIoSample
            {
                Pid = pair.Key,
                ReadBytesPerSec = readAvg,
                WriteBytesPerSec = writeAvg,
                ServiceTimeMs = serviceAvg,
                IoCount = iopsAvg
            });
        }

        LastSnapshotItems = list.Count;
        return list;
    }

    public void Dispose()
    {
        try
        {
            _session.Dispose();
        }
        catch
        {
            // 忽略关闭过程中的异常
        }
    }
}
