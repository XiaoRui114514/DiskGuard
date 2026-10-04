using System.Diagnostics;

namespace DiskGuard.App.Services;

/// <summary>只暂停可选的后台服务；不修改启动类型，重启后由 Windows 按原设置恢复。</summary>
public static class ServiceControlService
{
    public static (bool Ok, string Message) Stop(string serviceName)
    {
        var (exitCode, output) = Run("sc.exe", $"stop {serviceName}");
        return exitCode == 0
            ? (true, $"{serviceName}: {output.Trim()}")
            : (false, $"{serviceName}: {output.Trim()}");
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
            if (process == null) return (-1, "无法启动服务控制命令");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, "服务控制命令超时");
            }

            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
