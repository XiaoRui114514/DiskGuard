using System.Diagnostics;
using DiskGuard.Core.Engine;

namespace DiskGuard.App.Services;

/// <summary>普通读数每两秒绘制；保护状态、目标、磁盘与数据源变化立即放行。</summary>
public sealed class UiRefreshGate
{
    private EngineSnapshot? _last;
    private long _lastAt;

    public bool ShouldRefresh(EngineSnapshot snapshot, long timestamp)
    {
        bool changed = _last == null || snapshot.Paused != _last.Paused ||
                       snapshot.StateKind != _last.StateKind || snapshot.ActivePid != _last.ActivePid || snapshot.ActiveLevel != _last.ActiveLevel ||
                       snapshot.DiskNumber != _last.DiskNumber ||
                       snapshot.SelectedDiskAvailable != _last.SelectedDiskAvailable ||
                       snapshot.IoPrecise != _last.IoPrecise || snapshot.IoMode != _last.IoMode || snapshot.IoHealthError != _last.IoHealthError;
        if (!changed && Stopwatch.GetElapsedTime(_lastAt, timestamp).TotalSeconds < 2) return false;
        _last = snapshot;
        _lastAt = timestamp;
        return true;
    }

    public void Reset() => _last = null;
}
