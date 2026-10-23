using System.Text.Json;

namespace DiskGuard.App.Services;

public readonly record struct ScheduledTaskStatus(bool Available, bool Enabled, string State, string Error);

/// <summary>控制 Windows 自带的计划磁盘优化任务；不改服务启动类型。</summary>
public static class ScheduledTaskService
{
    public static ScheduledTaskStatus GetStatus()
    {
        const string script = """
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
try {
    $task = Get-ScheduledTask -TaskName 'ScheduledDefrag' -TaskPath '\Microsoft\Windows\Defrag\' -ErrorAction Stop
    [pscustomobject]@{
        Available = $true
        Enabled = ([string]$task.State -ne 'Disabled')
        State = [string]$task.State
        Error = ''
    } | ConvertTo-Json -Compress
}
catch {
    [pscustomobject]@{
        Available = $false
        Enabled = $false
        State = ''
        Error = [string]$_.Exception.Message
    } | ConvertTo-Json -Compress
}
""";
        var (exitCode, stdout, stderr) = PowerShellRunner.Run(script);
        if (exitCode != 0)
            return new ScheduledTaskStatus(false, false, string.Empty,
                string.IsNullOrWhiteSpace(stderr) ? $"PowerShell exit code {exitCode}" : stderr.Trim());

        try
        {
            var dto = JsonSerializer.Deserialize<TaskStatusDto>(
                ExtractJson(stdout), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return dto == null
                ? new ScheduledTaskStatus(false, false, string.Empty, "Task status could not be parsed")
                : new ScheduledTaskStatus(dto.Available, dto.Enabled, dto.State ?? string.Empty, dto.Error ?? string.Empty);
        }
        catch (Exception ex)
        {
            return new ScheduledTaskStatus(false, false, string.Empty, ex.Message);
        }
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

    private static string ExtractJson(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)].Trim() : string.Empty;
    }

    private sealed class TaskStatusDto
    {
        public bool Available { get; set; }
        public bool Enabled { get; set; }
        public string? State { get; set; }
        public string? Error { get; set; }
    }
}
