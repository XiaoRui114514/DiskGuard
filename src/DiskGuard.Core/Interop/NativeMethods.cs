using System.Runtime.InteropServices;
using System.Text;

namespace DiskGuard.Core.Interop;

internal static class NativeMethods
{
    // ---- 进程访问权限 ----
    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_SUSPEND_RESUME = 0x0800;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    internal const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    internal const uint IDLE_PRIORITY_CLASS = 0x00000040;
    internal const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    internal const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    internal const uint HIGH_PRIORITY_CLASS = 0x00000080;

    internal const int ProcessIoPriority = 33;

    // 磁盘 IO 优先级: 0=VeryLow 1=Low 2=Normal 3=High
    internal const int IoPriorityVeryLow = 0;
    internal const int IoPriorityLow = 1;
    internal const int IoPriorityNormal = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    // ---- 进程快照（取父子关系，用于"保护同一个程序的全部进程"）----
    internal const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS ioCounters);

    [DllImport("ntdll.dll")]
    internal static extern int NtSetInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, out int processInformation, int processInformationLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    internal static extern int NtResumeProcess(IntPtr processHandle);

    // ---- 作业对象限速 ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_IO_RATE_CONTROL_INFORMATION
    {
        public long MaxIops;
        public long MaxBandwidth;
        public long ReservationIops;
        public IntPtr VolumeName;
        public uint BaseIoSize;
        public uint ControlFlags;
    }

    internal const uint JOBOBJECT_IO_RATE_CONTROL_ENABLE = 0x1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetIoRateControlInformationJobObject(IntPtr hJob, ref JOBOBJECT_IO_RATE_CONTROL_INFORMATION lpIoRateControlInformation);

    // ---- 前台窗口 ----
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- 特权 ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    // ---- PDH ----
    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    internal const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
    internal const uint PDH_MORE_DATA = 0x800007D2;
    internal const uint PDH_INSUFFICIENT_BUFFER = 0x800007D3;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr SzName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhLookupPerfNameByIndexW(string? szMachineName, uint dwNameIndex, StringBuilder szNameBuffer, ref uint pcchNameBufferSize);
}
