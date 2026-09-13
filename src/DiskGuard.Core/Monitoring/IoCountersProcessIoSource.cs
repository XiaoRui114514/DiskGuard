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
    private readonly Dictionary<int, (ulong Read, ulong Write, ulong ReadOps, ulong WriteOps)> _last = new();

    public string Mode => Loc.T(LK.IoModeCounters);

    public bool IsPrecise => false;

    public bool ProvidesServiceTime => false;

    public string ShareMetricName => Loc.T(LK.MetricBytesApprox);

    public IReadOnlyList<ProcessIoSample> Snapshot(double elapsedSeconds)
    {
        double seconds = Math.Max(0.2, elapsedSeconds);
        var result = new List<ProcessIoSample>(256);
        var alive = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                int pid = process.Id;
                if (pid <= 0) continue;

                alive.Add(pid);
                string name;
                try { name = process.ProcessName; }
                catch { name = $"PID {pid}"; }

                IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle == IntPtr.Zero) continue;

                try
                {
                    if (!NativeMethods.GetProcessIoCounters(handle, out var counters)) continue;

                    if (_last.TryGetValue(pid, out var previous))
                    {
                        ulong read = counters.ReadTransferCount >= previous.Read ? counters.ReadTransferCount - previous.Read : 0;
                        ulong write = counters.WriteTransferCount >= previous.Write ? counters.WriteTransferCount - previous.Write : 0;
                        ulong readOps = counters.ReadOperationCount >= previous.ReadOps ? counters.ReadOperationCount - previous.ReadOps : 0;
                        ulong writeOps = counters.WriteOperationCount >= previous.WriteOps ? counters.WriteOperationCount - previous.WriteOps : 0;

                        if (read > 0 || write > 0 || readOps > 0 || writeOps > 0)
                        {
                            result.Add(new ProcessIoSample
                            {
                                Pid = pid,
                                Name = name,
                                ReadBytesPerSec = read / seconds,
                                WriteBytesPerSec = write / seconds,
                                IoCount = readOps + writeOps
                            });
                        }
                    }

                    _last[pid] = (counters.ReadTransferCount, counters.WriteTransferCount,
                                  counters.ReadOperationCount, counters.WriteOperationCount);
                }
                finally
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

        if (_last.Count > alive.Count + 64)
        {
            foreach (int pid in _last.Keys.Where(p => !alive.Contains(p)).ToList())
                _last.Remove(pid);
        }

        return result;
    }

    public void Dispose()
    {
        _last.Clear();
    }
}
