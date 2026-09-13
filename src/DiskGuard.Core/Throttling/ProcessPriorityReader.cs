using DiskGuard.Core.Interop;

namespace DiskGuard.Core.Throttling;

/// <summary>读取任意进程当前的磁盘 IO 优先级与 CPU 优先级（用于界面展示与自检）。</summary>
public static class ProcessPriorityReader
{
    public static (int IoPriority, uint PriorityClass) Read(int pid)
    {
        var (io, priorityClass, _) = ReadDetailed(pid);
        return (io, priorityClass);
    }

    /// <summary>读取优先级，同时返回 IO 优先级查询的 NTSTATUS（用于自检）。</summary>
    public static (int IoPriority, uint PriorityClass, int IoStatus) ReadDetailed(int pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return (-1, 0, unchecked((int)0x80070005));

        try
        {
            int io = -1;
            int status = NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessIoPriority, out int value, sizeof(int));
            if (status == 0) io = value;

            uint priorityClass = NativeMethods.GetPriorityClass(handle);
            return (io, priorityClass, status);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public static string IoPriorityText(int value) => value switch
    {
        0 => "极低",
        1 => "低",
        2 => "普通",
        3 => "高",
        _ => "未知"
    };
}
