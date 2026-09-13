namespace DiskGuard.Core.Monitoring;

public sealed class ProcessIoSample
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public double ReadBytesPerSec { get; init; }
    public double WriteBytesPerSec { get; init; }
    public double TotalBytesPerSec => ReadBytesPerSec + WriteBytesPerSec;

    /// <summary>窗口内累计的磁盘服务时间（毫秒），用于计算"占磁盘忙碌时间的比例"。</summary>
    public double ServiceTimeMs { get; init; }

    /// <summary>窗口内的磁盘 IO 次数。</summary>
    public double IoCount { get; init; }
}

/// <summary>按进程提供磁盘读写速率的数据源。</summary>
public interface IProcessIoSource : IDisposable
{
    /// <summary>数据来源说明（用于界面展示）。</summary>
    string Mode { get; }

    /// <summary>是否为精确的“磁盘0实际读写”统计（ETW）；false 表示含网络 IO 的近似值。</summary>
    bool IsPrecise { get; }

    /// <summary>是否能提供每个 IO 的服务时间（用于计算"占磁盘忙碌时间"的比例）。</summary>
    bool ProvidesServiceTime { get; }

    /// <summary>占比指标名称（用于界面说明）。</summary>
    string ShareMetricName { get; }

    /// <summary>数据源健康状态（空字符串表示正常）。</summary>
    string HealthError => string.Empty;

    /// <summary>返回自上次调用以来（elapsedSeconds 秒）各进程的平均速率。</summary>
    IReadOnlyList<ProcessIoSample> Snapshot(double elapsedSeconds);
}
