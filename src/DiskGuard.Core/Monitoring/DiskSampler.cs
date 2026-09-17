using DiskGuard.Core.Localization;

namespace DiskGuard.Core.Monitoring;

public sealed class DiskStatus
{
    public string InstanceName { get; init; } = string.Empty;
    public int DiskNumber { get; init; } = -1;
    public string Drives { get; init; } = string.Empty;
    public double BusyPercent { get; set; }
    public double QueueLength { get; set; }
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }

    /// <summary>平均每次读写操作的响应时间（秒），即 PDH 的 Avg. Disk sec/Transfer。</summary>
    public double LatencySeconds { get; set; }

    /// <summary>本次采样里有没有读到响应时间计数器（读不到时界面和判定都退回"未知"）。</summary>
    public bool LatencyValid { get; set; }

    /// <summary>平均响应时间（毫秒）。卡死时通常是几百毫秒到几秒。</summary>
    public double LatencyMs => LatencySeconds * 1000.0;

    public string DisplayName
    {
        get
        {
            if (DiskNumber < 0) return Loc.T(LK.DiskAllDisks);
            return Drives.Length > 0
                ? Loc.F(LK.DiskLabelWithDrivesFormat, DiskNumber, Drives)
                : Loc.F(LK.DiskLabelFormat, DiskNumber);
        }
    }
}

/// <summary>通过 PDH 采样物理磁盘忙率、队列长度与吞吐。</summary>
public sealed class DiskSampler : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter _idleCounter;
    private readonly PdhCounter _queueCounter;
    private readonly PdhCounter _readCounter;
    private readonly PdhCounter _writeCounter;
    private readonly PdhCounter? _latencyCounter;
    private readonly object _sync = new();
    private readonly Dictionary<string, DiskStatus> _statusByInstance = new();
    private List<DiskStatus> _current = new();

    public DiskSampler()
    {
        _idleCounter = _query.AddCounter(@"\PhysicalDisk(*)\% Idle Time");
        _queueCounter = _query.AddCounter(@"\PhysicalDisk(*)\Avg. Disk Queue Length");
        _readCounter = _query.AddCounter(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _writeCounter = _query.AddCounter(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        // 响应时间是判断"磁盘真的卡住"还是"忙率虚高"的关键指标：忙率 100% 但响应只有几毫秒，
        // 说明磁盘其实跟得上（限速它反而帮倒忙）；响应几百毫秒才是用户能感觉到的卡。
        // 个别系统没有这个计数器，取不到就退化成"响应未知"，不影响其它判定。
        try { _latencyCounter = _query.AddCounter(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer"); }
        catch { _latencyCounter = null; }
        _query.Collect();
    }

    public void Collect()
    {
        _query.Collect();
        if (_query.CollectCount < 2) return;

        var idle = _idleCounter.ReadArray();
        var queue = _queueCounter.ReadArray();
        var read = _readCounter.ReadArray();
        var write = _writeCounter.ReadArray();
        var latency = _latencyCounter?.ReadArray() ?? new List<(string, double)>();

        var list = new List<DiskStatus>(idle.Count);
        foreach (var (instance, idleValue) in idle)
        {
            // 复用同一个 DiskStatus 实例：界面下拉框不会因为每秒重建对象而丢失选中项
            if (!_statusByInstance.TryGetValue(instance, out var status))
            {
                status = ParseInstance(instance);
                _statusByInstance[instance] = status;
            }

            status.BusyPercent = Math.Clamp(100.0 - idleValue, 0.0, 100.0);
            status.QueueLength = Lookup(queue, instance);
            status.ReadBytesPerSec = Lookup(read, instance);
            status.WriteBytesPerSec = Lookup(write, instance);
            if (TryLookup(latency, instance, out double latencySeconds))
            {
                status.LatencySeconds = Math.Clamp(latencySeconds, 0, 60);
                status.LatencyValid = true;
            }
            else
            {
                status.LatencySeconds = 0;
                status.LatencyValid = false;
            }
            list.Add(status);
        }

        list.Sort((a, b) => a.DiskNumber.CompareTo(b.DiskNumber));
        lock (_sync) _current = list;
    }

    public IReadOnlyList<DiskStatus> Disks
    {
        get { lock (_sync) return _current; }
    }

    public DiskStatus? GetDisk(int diskNumber)
    {
        lock (_sync)
        {
            foreach (var status in _current)
            {
                if (status.DiskNumber == diskNumber) return status;
            }

            // 这一次采样里没有目标磁盘（PDH 偶发丢样本 / 磁盘刚被拔出）：
            // 只有"所有实例都解析不出磁盘编号"时才退回第一块，避免整个工具失效；
            // 只要能解析出编号就绝不拿别的磁盘顶替——否则会出现"监控磁盘 0，实际读的是磁盘 1 的忙率"，
            // 于是把一个空闲磁盘报成 100% 忙，并据此去限速无辜的进程。
            foreach (var status in _current)
            {
                if (status.DiskNumber >= 0) return null;
            }

            return _current.FirstOrDefault();
        }
    }

    private static double Lookup(List<(string Instance, double Value)> source, string instance)
    {
        foreach (var item in source)
            if (string.Equals(item.Instance, instance, StringComparison.OrdinalIgnoreCase))
                return item.Value;
        return 0;
    }

    private static bool TryLookup(List<(string Instance, double Value)> source, string instance, out double value)
    {
        foreach (var item in source)
        {
            if (!string.Equals(item.Instance, instance, StringComparison.OrdinalIgnoreCase)) continue;
            value = item.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private static DiskStatus ParseInstance(string instance)
    {
        if (string.Equals(instance, "_total", StringComparison.OrdinalIgnoreCase))
            return new DiskStatus { InstanceName = instance, DiskNumber = -1 };

        var tokens = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int diskNumber = -1;
        var drives = new List<string>();

        foreach (var token in tokens)
        {
            if (token.EndsWith(':') && token.Length == 2) drives.Add(token.ToUpperInvariant());
            else if (int.TryParse(token, out int number)) diskNumber = number;
        }

        return new DiskStatus
        {
            InstanceName = instance,
            DiskNumber = diskNumber,
            Drives = string.Join(" ", drives)
        };
    }

    public void Dispose() => _query.Dispose();
}
