using System.Diagnostics;
using DiskGuard.Core.Interop;
using DiskGuard.Core.Localization;

namespace DiskGuard.Core.Monitoring;

/// <summary>
/// 使用 Windows 进程 IO 计数器（GetProcessIoCounters）统计每个进程的读写速率。
/// 未提权时的回退方案：数值包含网络 IO，属于近似统计，但实例/PID 映射稳定可靠。
/// </summary>
public sealed class IoCountersProcessIoSource : IProcessIoSource
{
    // 普通用户模式下每秒枚举并 OpenProcess 全部进程，会在开机阶段额外制造大量内核调用。
    // 进程集合变化没有那么快：句柄和 PID 映射短暂缓存，仍每秒读取计数器，但每 3 秒才重建一次集合。
    private const int ProcessRefreshSeconds = 3;

    private sealed class Entry
    {
        public string Name = string.Empty;
        public IntPtr Handle;
        public ulong Read;
        public ulong Write;
        public ulong ReadOps;
        public ulong WriteOps;
    }

    private readonly Dictionary<int, Entry> _entries = new();
    private DateTime _nextRefreshUtc = DateTime.MinValue;

    public string Mode => Loc.T(LK.IoModeCounters);

    public bool IsPrecise => false;

    public bool ProvidesServiceTime => false;

    public string ShareMetricName => Loc.T(LK.MetricBytesApprox);

    public IReadOnlyList<ProcessIoSample> Snapshot(double elapsedSeconds)
    {
        double seconds = Math.Max(0.2, elapsedSeconds);
        var result = new List<ProcessIoSample>(256);

        if (DateTime.UtcNow >= _nextRefreshUtc)
        {
            RefreshEntries();
            _nextRefreshUtc = DateTime.UtcNow.AddSeconds(ProcessRefreshSeconds);
        }

        foreach (var pair in _entries.ToArray())
        {
            int pid = pair.Key;
            Entry entry = pair.Value;
            try
            {
                if (entry.Handle == IntPtr.Zero || !NativeMethods.GetProcessIoCounters(entry.Handle, out var counters)) continue;

                ulong read = counters.ReadTransferCount >= entry.Read ? counters.ReadTransferCount - entry.Read : 0;
                ulong write = counters.WriteTransferCount >= entry.Write ? counters.WriteTransferCount - entry.Write : 0;
                ulong readOps = counters.ReadOperationCount >= entry.ReadOps ? counters.ReadOperationCount - entry.ReadOps : 0;
                ulong writeOps = counters.WriteOperationCount >= entry.WriteOps ? counters.WriteOperationCount - entry.WriteOps : 0;

                if (read > 0 || write > 0 || readOps > 0 || writeOps > 0)
                {
                    result.Add(new ProcessIoSample
                    {
                        Pid = pid,
                        Name = entry.Name,
                        ReadBytesPerSec = read / seconds,
                        WriteBytesPerSec = write / seconds,
                        CurrentBytesPerSec = (read + write) / seconds,
                        IoCount = ((double)readOps + writeOps) / seconds,
                        CurrentIoCount = ((double)readOps + writeOps) / seconds
                    });
                }

                entry.Read = counters.ReadTransferCount;
                entry.Write = counters.WriteTransferCount;
                entry.ReadOps = counters.ReadOperationCount;
                entry.WriteOps = counters.WriteOperationCount;
            }
            catch
            {
                // 进程退出或句柄失效时等下一次刷新重建，当前轮不阻塞其它进程。
            }
        }

        return result;
    }

    private void RefreshEntries()
    {
        var seen = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                int pid = process.Id;
                if (pid <= 0) continue;
                seen.Add(pid);
                string name;
                try { name = process.ProcessName; }
                catch { name = $"PID {pid}"; }
                if (_entries.TryGetValue(pid, out var existing))
                {
                    if (existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    NativeMethods.CloseHandle(existing.Handle);
                    _entries.Remove(pid);
                }

                IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle == IntPtr.Zero) continue;

                var entry = new Entry { Name = name, Handle = handle };
                // 以加入缓存这一刻作为基线，避免首次采样把进程自启动以来的累计 IO 误报成瞬时速率。
                if (NativeMethods.GetProcessIoCounters(handle, out var counters))
                {
                    entry.Read = counters.ReadTransferCount;
                    entry.Write = counters.WriteTransferCount;
                    entry.ReadOps = counters.ReadOperationCount;
                    entry.WriteOps = counters.WriteOperationCount;
                    _entries[pid] = entry;
                }
                else
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
            catch
            {
                // 忽略访问失败或进程已退出
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (int pid in _entries.Keys.Where(pid => !seen.Contains(pid)).ToList())
        {
            NativeMethods.CloseHandle(_entries[pid].Handle);
            _entries.Remove(pid);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
            NativeMethods.CloseHandle(entry.Handle);
        _entries.Clear();
    }
}
