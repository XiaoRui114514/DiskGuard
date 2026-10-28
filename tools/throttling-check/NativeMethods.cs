using System.Runtime.InteropServices;

namespace DiskGuard.Core.Interop;

// This substitute keeps the checks portable and never changes a real process's settings.
// Constants/types unused by ApplyLevel2 are included to compile the unmodified throttler source.
internal static class NativeMethods
{
    internal const uint PROCESS_TERMINATE = 1, PROCESS_SET_QUOTA = 0x100,
        PROCESS_SET_INFORMATION = 0x200, PROCESS_QUERY_INFORMATION = 0x400,
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_SUSPEND_RESUME = 0x800,
        BELOW_NORMAL_PRIORITY_CLASS = 0x4000, JOBOBJECT_IO_RATE_CONTROL_ENABLE = 1;
    internal const int ProcessIoPriority = 33, IoPriorityNormal = 2;

    internal struct JOBOBJECT_IO_RATE_CONTROL_INFORMATION
    {
        public long MaxIops, MaxBandwidth, ReservationIops;
        public IntPtr VolumeName;
        public uint BaseIoSize, ControlFlags;
    }

    internal static uint RateControlError;
    internal static int LastWin32Error;
    internal static int AssignCalls;
    internal static bool AssignSucceeds = true;
    internal static IntPtr CreateJobObjectW(IntPtr attributes, string? name) => new(3);
    internal static uint SetIoRateControlInformationJobObject(IntPtr job, ref JOBOBJECT_IO_RATE_CONTROL_INFORMATION info)
    {
        Marshal.SetLastPInvokeError(LastWin32Error);
        return RateControlError;
    }
    internal static bool AssignProcessToJobObject(IntPtr job, IntPtr process)
    {
        AssignCalls++;
        Marshal.SetLastPInvokeError(LastWin32Error);
        return AssignSucceeds;
    }
    internal static bool CloseHandle(IntPtr handle) => true;
    internal static IntPtr OpenProcess(uint access, bool inherit, int pid) => throw new NotSupportedException();
    internal static int NtQueryInformationProcess(IntPtr handle, int infoClass, out int info, int size) => throw new NotSupportedException();
    internal static int NtSetInformationProcess(IntPtr handle, int infoClass, ref int info, int size) => throw new NotSupportedException();
    internal static uint GetPriorityClass(IntPtr handle) => throw new NotSupportedException();
    internal static bool SetPriorityClass(IntPtr handle, uint priority) => throw new NotSupportedException();
    internal static int NtSuspendProcess(IntPtr handle) => throw new NotSupportedException();
    internal static int NtResumeProcess(IntPtr handle) => throw new NotSupportedException();
}
