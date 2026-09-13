using System.Security.Principal;

namespace DiskGuard.Core.Interop;

public static class PrivilegeHelper
{
    private static bool _privilegesEnabled;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>启用 SeDebugPrivilege / SeIncreaseBasePriorityPrivilege，用于操作系统进程。</summary>
    public static bool EnableDebugPrivileges()
    {
        if (_privilegesEnabled) return true;
        bool ok = Enable("SeDebugPrivilege");
        Enable("SeIncreaseBasePriorityPrivilege");
        _privilegesEnabled = ok;
        return ok;
    }

    private static bool Enable(string name)
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out var token))
            return false;

        try
        {
            if (!NativeMethods.LookupPrivilegeValueW(null, name, out var luid)) return false;

            var tp = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LUID_AND_ATTRIBUTES { Luid = luid, Attributes = NativeMethods.SE_PRIVILEGE_ENABLED }
            };

            if (!NativeMethods.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // 当部分特权未能调整时会返回 ERROR_NOT_ALL_ASSIGNED (1300)，通过 GetLastError 判断
            return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
