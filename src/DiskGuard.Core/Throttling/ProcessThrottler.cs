using System.ComponentModel;
using System.Diagnostics;
using DiskGuard.Core.Interop;

namespace DiskGuard.Core.Throttling;

/// <summary>
/// 分级限速：
/// 一级 = 降低磁盘 IO 优先级（必要时同时降低 CPU 优先级），无感、可随时还原；
/// 二级 = 通过作业对象 IO 速率控制设置磁盘吞吐上限（缓存写入）；
/// 三级 = 间歇挂起（默认关闭，能让磁盘立即让路，但目标程序会瞬时无响应）。
/// </summary>
public sealed class ProcessThrottler
{
    private const uint FullAccess =
        NativeMethods.PROCESS_TERMINATE |
        NativeMethods.PROCESS_SET_QUOTA |
        NativeMethods.PROCESS_SET_INFORMATION |
        NativeMethods.PROCESS_QUERY_INFORMATION |
        NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION |
        NativeMethods.PROCESS_SUSPEND_RESUME;

    private const uint SoftAccess =
        NativeMethods.PROCESS_SET_INFORMATION |
        NativeMethods.PROCESS_QUERY_INFORMATION |
        NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION;

    public int LastError { get; private set; }

    public string LastErrorText => LastError == 0 ? string.Empty : new Win32Exception(LastError).Message;

    public ThrottleHandle? ApplyLevel1(int pid, string name, bool lowerIoPriority, int ioPriority, bool lowerCpuPriority)
    {
        IntPtr handle = NativeMethods.OpenProcess(FullAccess, false, pid);
        if (handle == IntPtr.Zero)
            handle = NativeMethods.OpenProcess(SoftAccess, false, pid);

        if (handle == IntPtr.Zero)
        {
            LastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return null;
        }

        var result = new ThrottleHandle { Pid = pid, Name = name, ProcessHandle = handle };
        bool appliedAnything = false;

        if (lowerIoPriority)
        {
            if (NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessIoPriority, out int current, sizeof(int)) == 0)
                result.OriginalIoPriority = current;

            int target = ioPriority;
            if (NativeMethods.NtSetInformationProcess(handle, NativeMethods.ProcessIoPriority, ref target, sizeof(int)) == 0)
            {
                result.AppliedIoPriority = target;
                appliedAnything = true;
            }
            else
            {
                LastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            }
        }

        if (lowerCpuPriority)
        {
            uint before = NativeMethods.GetPriorityClass(handle);
            if (before != 0) result.OriginalPriorityClass = before;

            if (NativeMethods.SetPriorityClass(handle, NativeMethods.BELOW_NORMAL_PRIORITY_CLASS))
            {
                result.AppliedPriorityClass = NativeMethods.BELOW_NORMAL_PRIORITY_CLASS;
                appliedAnything = true;
            }
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            result.ProcessStartTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
            // 部分系统进程无法读取启动时间，忽略
        }

        if (!appliedAnything)
        {
            NativeMethods.CloseHandle(handle);
            return null;
        }

        return result;
    }

    /// <summary>
    /// 施加作业对象速率上限。<paramref name="maxIops"/> 是必须的：
    /// 4K 随机等小 IO 场景下，进程可能只有几百 KB/s 却把磁盘占满，
    /// 只限吞吐（字节/秒）根本拦不住，必须同时限制每秒 IO 次数。
    /// </summary>
    public bool ApplyLevel2(ThrottleHandle handle, long bytesPerSec, long maxIops)
    {
        if (!handle.HasHandle || (bytesPerSec <= 0 && maxIops <= 0)) return false;

        if (handle.JobHandle == IntPtr.Zero)
        {
            handle.JobHandle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            if (handle.JobHandle == IntPtr.Zero)
            {
                LastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return false;
            }
        }

        var info = new NativeMethods.JOBOBJECT_IO_RATE_CONTROL_INFORMATION
        {
            MaxIops = Math.Max(0, maxIops),
            MaxBandwidth = Math.Max(0, bytesPerSec),
            ReservationIops = 0,
            VolumeName = IntPtr.Zero,
            BaseIoSize = 0,
            ControlFlags = NativeMethods.JOBOBJECT_IO_RATE_CONTROL_ENABLE
        };

        if (!NativeMethods.SetIoRateControlInformationJobObject(handle.JobHandle, ref info))
        {
            LastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        if (!NativeMethods.AssignProcessToJobObject(handle.JobHandle, handle.ProcessHandle))
        {
            LastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            DisableJobRateControl(handle);
            return false;
        }

        handle.CapBytesPerSec = Math.Max(0, bytesPerSec);
        handle.CapIops = Math.Max(0, maxIops);
        handle.IsLevel2 = true;
        handle.Level = 2;
        LastError = 0;
        return true;
    }

    public void DisableLevel2(ThrottleHandle handle)
    {
        DisableJobRateControl(handle);
        handle.IsLevel2 = false;
        handle.CapBytesPerSec = 0;
        handle.CapIops = 0;
        if (handle.Level >= 2) handle.Level = 1;
    }

    private void DisableJobRateControl(ThrottleHandle handle)
    {
        if (handle.JobHandle == IntPtr.Zero) return;

        var info = new NativeMethods.JOBOBJECT_IO_RATE_CONTROL_INFORMATION
        {
            MaxIops = 0,
            MaxBandwidth = 0,
            ReservationIops = 0,
            VolumeName = IntPtr.Zero,
            BaseIoSize = 0,
            ControlFlags = 0
        };
        NativeMethods.SetIoRateControlInformationJobObject(handle.JobHandle, ref info);
        NativeMethods.CloseHandle(handle.JobHandle);
        handle.JobHandle = IntPtr.Zero;
    }

    public bool StartLevel3(ThrottleHandle handle, int runMs, int pauseMs)
    {
        if (!handle.HasHandle) return false;
        if (handle.SuspendCts != null) return true;

        var cts = new CancellationTokenSource();
        handle.SuspendCts = cts;
        handle.IsLevel3 = true;
        handle.Level = 3;
        runMs = Math.Clamp(runMs, 50, 5000);
        pauseMs = Math.Clamp(pauseMs, 20, 5000);

        _ = Task.Factory.StartNew(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (NativeMethods.NtSuspendProcess(handle.ProcessHandle) != 0) break;

                    try { await Task.Delay(pauseMs, cts.Token).ConfigureAwait(false); }
                    catch (TaskCanceledException) { }

                    NativeMethods.NtResumeProcess(handle.ProcessHandle);

                    try { await Task.Delay(runMs, cts.Token).ConfigureAwait(false); }
                    catch (TaskCanceledException) { }
                }
            }
            finally
            {
                NativeMethods.NtResumeProcess(handle.ProcessHandle);
            }
        }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return true;
    }

    public void StopLevel3(ThrottleHandle handle)
    {
        try { handle.SuspendCts?.Cancel(); } catch { }
        handle.SuspendCts = null;
        handle.IsLevel3 = false;
        if (handle.Level >= 3) handle.Level = handle.IsLevel2 ? 2 : 1;
        if (handle.HasHandle) NativeMethods.NtResumeProcess(handle.ProcessHandle);
    }

    public void Release(ThrottleHandle handle)
    {
        StopLevel3(handle);
        DisableLevel2(handle);

        if (!handle.HasHandle) return;

        if (handle.AppliedPriorityClass != 0 && handle.OriginalPriorityClass != 0)
        {
            uint current = NativeMethods.GetPriorityClass(handle.ProcessHandle);
            if (current == handle.AppliedPriorityClass)
                NativeMethods.SetPriorityClass(handle.ProcessHandle, handle.OriginalPriorityClass);
        }

        if (handle.AppliedIoPriority >= 0)
        {
            bool needRestore = true;

            // 能查询到原值且当前值仍是我们设置的值时，精确还原；查不到（部分环境限制）则还原为系统默认。
            if (NativeMethods.NtQueryInformationProcess(handle.ProcessHandle, NativeMethods.ProcessIoPriority, out int current, sizeof(int)) == 0)
                needRestore = current == handle.AppliedIoPriority;

            if (needRestore)
            {
                int original = handle.OriginalIoPriority >= 0 ? handle.OriginalIoPriority : NativeMethods.IoPriorityNormal;
                NativeMethods.NtSetInformationProcess(handle.ProcessHandle, NativeMethods.ProcessIoPriority, ref original, sizeof(int));
            }
        }

        NativeMethods.CloseHandle(handle.ProcessHandle);
        handle.ProcessHandle = IntPtr.Zero;
        handle.Level = 0;
    }

    /// <summary>按状态文件还原上次异常退出时残留的限速。</summary>
    public static bool RestoreStale(int pid, int appliedIo, int originalIo, uint appliedCpu, uint originalCpu)
    {
        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_INFORMATION | NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false, pid);
        if (handle == IntPtr.Zero) return false;

        bool restored = false;
        try
        {
            if (appliedIo >= 0)
            {
                bool needRestore = true;
                if (NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessIoPriority, out int current, sizeof(int)) == 0)
                    needRestore = current == appliedIo;

                if (needRestore)
                {
                    int original = originalIo >= 0 ? originalIo : NativeMethods.IoPriorityNormal;
                    restored |= NativeMethods.NtSetInformationProcess(handle, NativeMethods.ProcessIoPriority, ref original, sizeof(int)) == 0;
                }
            }

            if (appliedCpu != 0 && originalCpu != 0)
            {
                uint current = NativeMethods.GetPriorityClass(handle);
                if (current == appliedCpu)
                    restored |= NativeMethods.SetPriorityClass(handle, originalCpu);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }

        return restored;
    }
}
