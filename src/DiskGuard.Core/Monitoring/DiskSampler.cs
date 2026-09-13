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
    private readonly object _sync = new();
    private readonly Dictionary<string, DiskStatus> _statusByInstance = new();
    private List<DiskStatus> _current = new();

    public DiskSampler()
    {
        _idleCounter = _query.AddCounter(@"\PhysicalDisk(*)\% Idle Time");
        _queueCounter = _query.AddCounter(@"\PhysicalDisk(*)\Avg. Disk Queue Length");
        _readCounter = _query.AddCounter(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _writeCounter = _query.AddCounter(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
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
            var exact = _current.FirstOrDefault(d => d.DiskNumber == diskNumber);
            if (exact != null) return exact;
            return _current.FirstOrDefault(d => d.DiskNumber == 0) ?? _current.FirstOrDefault();
        }
    }

    private static double Lookup(List<(string Instance, double Value)> source, string instance)
    {
        foreach (var item in source)
            if (string.Equals(item.Instance, instance, StringComparison.OrdinalIgnoreCase))
                return item.Value;
        return 0;
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
