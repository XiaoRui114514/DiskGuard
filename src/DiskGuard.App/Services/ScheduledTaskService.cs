using System.Runtime.InteropServices;

namespace DiskGuard.App.Services;

public readonly record struct ScheduledTaskStatus(bool Available, bool Enabled, string State, string Error);

/// <summary>控制 Windows 自带的计划磁盘优化任务；不改服务启动类型。</summary>
public static class ScheduledTaskService
{
    public static ScheduledTaskStatus GetStatus()
    {
        object? scheduler = null, folder = null, task = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return new(false, false, string.Empty, "Task Scheduler is unavailable");
            scheduler = Activator.CreateInstance(type);
            if (scheduler == null) return new(false, false, string.Empty, "Task Scheduler could not be created");
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder(@"\Microsoft\Windows\Defrag");
            task = ((dynamic)folder).GetTask("ScheduledDefrag");
            bool enabled = ((dynamic)task).Enabled;
            int state = ((dynamic)task).State;
            string stateText = state switch
            {
                1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => "Unknown"
            };
            return new(true, enabled, stateText, string.Empty);
        }
        catch (Exception ex)
        {
            return new(false, false, string.Empty, ex.Message);
        }
        finally
        {
            ReleaseCom(task);
            ReleaseCom(folder);
            ReleaseCom(scheduler);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); } catch { }
    }

    public static DefenderOperationResult SetEnabled(bool enabled)
    {
        string action = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        string script = $$"""
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
try {
    $task = Get-ScheduledTask -TaskName 'ScheduledDefrag' -TaskPath '\Microsoft\Windows\Defrag\' -ErrorAction Stop
    {{action}} -InputObject $task -ErrorAction Stop | Out-Null
    'OK'
}
catch {
    'ERROR: ' + $_.Exception.Message
    exit 1
}
""";
        var (exitCode, stdout, stderr) = PowerShellRunner.Run(script);
        if (exitCode != 0 || !stdout.Contains("OK", StringComparison.Ordinal))
        {
            string message = stdout.Trim();
            if (message.StartsWith("ERROR:", StringComparison.Ordinal)) message = message[6..].Trim();
            if (message.Length == 0) message = stderr.Trim();
            return new DefenderOperationResult(false,
                message.Length == 0 ? $"PowerShell exit code {exitCode}" : message);
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var status = GetStatus();
            if (status.Available && status.Enabled == enabled)
                return new DefenderOperationResult(true, status.State);
            Thread.Sleep(200);
        }

        var final = GetStatus();
        return new DefenderOperationResult(false,
            final.Available ? $"Final task state: {final.State}" : final.Error);
    }

}
