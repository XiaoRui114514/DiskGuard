namespace DiskGuard.Core.Throttling;

/// <summary>一个被限速进程的状态与原始设置，用于精确还原。</summary>
public sealed class ThrottleHandle
{
    public int Pid { get; init; }
    public string Name { get; set; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime? ProcessStartTimeUtc { get; set; }

    internal IntPtr ProcessHandle { get; set; }

    public int OriginalIoPriority { get; internal set; } = -1;
    public int AppliedIoPriority { get; internal set; } = -1;

    public uint OriginalPriorityClass { get; internal set; }
    public uint AppliedPriorityClass { get; internal set; }

    internal IntPtr JobHandle { get; set; } = IntPtr.Zero;
    public bool IsLevel2 { get; internal set; }
    public long CapBytesPerSec { get; internal set; }

    public bool IsLevel3 { get; internal set; }
    internal CancellationTokenSource? SuspendCts { get; set; }

    public int Level { get; internal set; } = 1;
    public bool IsManual { get; set; }

    public bool HasHandle => ProcessHandle != IntPtr.Zero;
}
