using System.Diagnostics;
using DiskGuard.Core.Interop;

namespace DiskGuard.Core.Monitoring;

public readonly record struct ProcessResourceSample(long? MemoryBytes, double? IoBytesPerSecond);

/// <summary>按指定 PID 去重采样；只读取工作集与进程 I/O，句柄在本次采样结束时全部释放。</summary>
public static class ProcessResourceSampler
{
    private sealed class Entry
    {
        public IntPtr Handle;
        public long? MemoryBytes;
        public NativeMethods.IO_COUNTERS? Initial;
        public long StartedAt;
    }

    public static IReadOnlyDictionary<int, ProcessResourceSample> Sample(IEnumerable<int> processIds)
    {
        var entries = new Dictionary<int, Entry>();
        try
        {
            foreach (int pid in processIds.Where(pid => pid > 0).Distinct())
            {
                var entry = new Entry();
                entries[pid] = entry;
                entry.Handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (entry.Handle != IntPtr.Zero && NativeMethods.GetProcessIoCounters(entry.Handle, out var counters))
                {
                    entry.Initial = counters;
                    entry.StartedAt = Stopwatch.GetTimestamp();
                }
            }

            // GetProcessById + WorkingSet64 会反复读取系统进程表；一次快照为所有目标取值。
            // 只保存目标 PID 的内存，其他进程对象立即释放。
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            if (entries.TryGetValue(process.Id, out var entry)) entry.MemoryBytes = process.WorkingSet64;
                        }
                        catch { /* 进程退出或不可读：保持未知。 */ }
                    }
                }
            }
            catch { /* 进程表不可读不影响已成功打开的 I/O 计数器。 */ }

            if (entries.Values.Any(entry => entry.Initial.HasValue)) Thread.Sleep(250);
            var result = new Dictionary<int, ProcessResourceSample>();
            foreach (var (pid, entry) in entries)
            {
                double? rate = null;
                if (entry.Initial is { } initial && NativeMethods.GetProcessIoCounters(entry.Handle, out var current) &&
                    current.ReadTransferCount >= initial.ReadTransferCount && current.WriteTransferCount >= initial.WriteTransferCount)
                {
                    double seconds = Stopwatch.GetElapsedTime(entry.StartedAt).TotalSeconds;
                    if (seconds > 0)
                        rate = ((double)(current.ReadTransferCount - initial.ReadTransferCount) +
                                (current.WriteTransferCount - initial.WriteTransferCount)) / seconds;
                }
                result[pid] = new ProcessResourceSample(entry.MemoryBytes, rate);
            }
            return result;
        }
        finally
        {
            foreach (var entry in entries.Values)
                if (entry.Handle != IntPtr.Zero) NativeMethods.CloseHandle(entry.Handle);
        }
    }
}
