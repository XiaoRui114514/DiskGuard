using DiskGuard.Core.Localization;
using DiskGuard.Core.Monitoring;
using DiskGuard.Core.Util;

namespace DiskGuard.Core.Engine;

public sealed class ProcessIoRow
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }
    public double TotalBytesPerSec => ReadBytesPerSec + WriteBytesPerSec;

    /// <summary>窗口平均速率（用于判定是否值得限速，避免瞬时尖峰误伤）。</summary>
    public double AverageBytesPerSec { get; set; }

    /// <summary>窗口内累计磁盘服务时间（毫秒）。</summary>
    public double ServiceTimeMs { get; set; }

    /// <summary>窗口内磁盘 IO 次数。</summary>
    public double IoCount { get; set; }

    /// <summary>该进程让磁盘忙碌的时间百分比（0-100），核心判定指标。</summary>
    public double OccupancyPercent { get; set; }

    /// <summary>窗口平均占用百分比。</summary>
    public double AverageOccupancyPercent { get; set; }

    /// <summary>窗口平均 IO 次数（次/秒）。</summary>
    public double AverageIoCount { get; set; }

    /// <summary>界面标记：已限速 / 系统内核 / 保护名单 / 前台程序 / 空。</summary>
    public string Tag { get; set; } = string.Empty;

    public string RateText => ProcessUtil.FormatRate(TotalBytesPerSec);
    public string ReadText => ProcessUtil.FormatRate(ReadBytesPerSec);
    public string WriteText => ProcessUtil.FormatRate(WriteBytesPerSec);
    public string OccupancyText => OccupancyPercent <= 0 ? "-" : $"{OccupancyPercent:0.#}%";
}

public sealed class EngineSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public double BusyPercent { get; init; }
    public double QueueLength { get; init; }
    public double ReadBytesPerSec { get; init; }
    public double WriteBytesPerSec { get; init; }
    public IReadOnlyList<DiskStatus> Disks { get; init; } = Array.Empty<DiskStatus>();
    public int DiskNumber { get; init; }
    public string DiskLabel { get; init; } = string.Empty;

    public IReadOnlyList<ProcessIoRow> Rows { get; init; } = Array.Empty<ProcessIoRow>();

    public int ActivePid { get; init; } = -1;
    public string ActiveName { get; init; } = string.Empty;
    public int ActiveLevel { get; init; }
    public DateTime? ActiveSince { get; init; }
    public double ActiveRateBytesPerSec { get; init; }
    public long ActiveCapBytesPerSec { get; init; }

    /// <summary>状态类型：paused / idle / watch / throttled。</summary>
    public string StateKind { get; init; } = "idle";
    public string StateText { get; init; } = Loc.T(LK.StateMonitoring);

    public bool Paused { get; init; }
    public string IoMode { get; init; } = string.Empty;
    public bool IoPrecise { get; init; }
    public string ShareMetric { get; init; } = string.Empty;
    public double ActiveOccupancyPercent { get; init; }
    public double TriggerPercent { get; init; }
    public double RecoverPercent { get; init; }
    public double TriggerOccupancyPercent { get; init; }
    public double TargetOccupancyPercent { get; init; }
}
