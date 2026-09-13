using System.Diagnostics;
using System.IO;
using DiskGuard.Core.Localization;

namespace DiskGuard.App.Services;

/// <summary>通过计划任务实现"开机自动启动（管理员权限，免每次弹窗）"。</summary>
public static class AutoStartService
{
    private const string TaskName = "DiskGuard_AutoStart";

    public static bool IsEnabled()
    {
        var (exitCode, _) = Run("schtasks", $"/Query /TN \"{TaskName}\"");
        return exitCode == 0;
    }

    public static (bool Ok, string Message) Enable(string executablePath)
    {
        var (exitCode, output) = Run("schtasks",
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{executablePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        return exitCode == 0
            ? (true, Loc.T(LK.AutoStartEnabled))
            : (false, Loc.F(LK.AutoStartEnableFailedFormat, output.Trim()));
    }

    public static (bool Ok, string Message) Disable()
    {
        var (exitCode, output) = Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");
        return exitCode == 0
            ? (true, Loc.T(LK.AutoStartDisabled))
            : (false, Loc.F(LK.AutoStartDisableFailedFormat, output.Trim()));
    }

    private static (int ExitCode, string Output) Run(string file, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(info);
            if (process == null) return (-1, Loc.T(LK.AutoStartCannotLaunch));

            // 两个输出流必须同时异步读取：否则子进程输出较多时写满管道会一直等我们读，形成死锁
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, Loc.T(LK.AutoStartTimeout));
            }

            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DiskGuard.exe");
}
