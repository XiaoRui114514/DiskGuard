using System.Diagnostics;
using System.Windows;
using DiskGuard.Core.Interop;

namespace DiskGuard.App.Services;

public static class ElevationService
{
    public static bool IsElevated => PrivilegeHelper.IsElevated;

    /// <summary>以管理员身份重新启动本程序（UAC 静默/提示由系统策略决定）。</summary>
    public static bool RestartElevated()
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = AutoStartService.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("以管理员身份重启失败：" + ex.Message, "磁盘守护",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
