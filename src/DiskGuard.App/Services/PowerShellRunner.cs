using System.Diagnostics;
using System.Text;

namespace DiskGuard.App.Services;

/// <summary>通过隐藏的 Windows PowerShell 执行只含内置常量的脚本，避免命令行引号注入。</summary>
internal static class PowerShellRunner
{
    public static (int ExitCode, string Stdout, string Stderr) Run(string script, int timeoutMs = 20000)
    {
        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[]
                     {
                         "-NoLogo",
                         "-NoProfile",
                         "-NonInteractive",
                         "-WindowStyle", "Hidden",
                         "-EncodedCommand", encoded
                     })
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process == null) return (-1, string.Empty, "Unable to start powershell.exe");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty, "PowerShell timed out");
            }

            return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
