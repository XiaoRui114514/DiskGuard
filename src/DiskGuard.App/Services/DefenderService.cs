using System.Text.Json;

namespace DiskGuard.App.Services;

public sealed record DefenderStatus(
    bool Available,
    bool RealtimeEnabled,
    bool ServiceEnabled,
    bool TamperProtected,
    bool LowCpuPriority,
    bool ScanOnlyIfIdle,
    bool ScheduledScanEnabled,
    int CpuLimit,
    string Error);

public readonly record struct DefenderOperationResult(bool Ok, string Message);

/// <summary>
/// 使用 Windows 自带的 Defender PowerShell 设置控制扫描负载。
/// MsMpEng 是受保护的系统安全服务，本类不杀进程、不改 WinDefend 启动类型。
/// </summary>
public static class DefenderService
{
    public static DefenderStatus GetStatus()
    {
        const string script = """
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
try {
    $status = Get-MpComputerStatus
    $prefs = Get-MpPreference
    $engine = Get-Process -Name MsMpEng -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Available = $true
        RealtimeEnabled = [bool]$status.RealTimeProtectionEnabled
        ServiceEnabled = [bool]$status.AMServiceEnabled
        TamperProtected = [bool]$status.IsTamperProtected
        LowCpuPriority = [bool]$prefs.EnableLowCpuPriority
        ScanOnlyIfIdle = [bool]$prefs.ScanOnlyIfIdleEnabled
        ScheduledScanEnabled = (([int]$prefs.ScanScheduleDay) -ne 8)
        CpuLimit = [int]$prefs.ScanAvgCPULoadFactor
        Error = ''
    } | ConvertTo-Json -Compress
}
catch {
    [pscustomobject]@{
        Available = $false
        RealtimeEnabled = $false
        ServiceEnabled = $false
        TamperProtected = $false
        LowCpuPriority = $false
        ScanOnlyIfIdle = $false
        ScheduledScanEnabled = $false
        CpuLimit = 50
        Error = [string]$_.Exception.Message
    } | ConvertTo-Json -Compress
}
""";

        var (exitCode, stdout, stderr) = PowerShellRunner.Run(script);
        if (exitCode != 0)
            return Unavailable(string.IsNullOrWhiteSpace(stderr) ? $"PowerShell exit code {exitCode}" : stderr.Trim());

        string json = ExtractJson(stdout);
        if (json.Length == 0)
            return Unavailable(string.IsNullOrWhiteSpace(stderr) ? "Defender returned no status" : stderr.Trim());

        try
        {
            DefenderStatusDto? dto = JsonSerializer.Deserialize<DefenderStatusDto>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto == null) return Unavailable("Defender status could not be parsed");

            return new DefenderStatus(
                dto.Available,
                dto.RealtimeEnabled,
                dto.ServiceEnabled,
                dto.TamperProtected,
                dto.LowCpuPriority,
                dto.ScanOnlyIfIdle,
                dto.ScheduledScanEnabled,
                dto.CpuLimit is >= 1 and <= 100 ? dto.CpuLimit : 50,
                dto.Error ?? string.Empty);
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }
    }

    public static DefenderOperationResult SetRealtimeProtection(bool enabled)
    {
        string command = "Set-MpPreference -DisableRealtimeMonitoring " +
                         ToPowerShellBoolean(!enabled) + " -ErrorAction Stop";
        var result = RunPreference(command);
        return result.Ok ? Verify(s => s.RealtimeEnabled == enabled, "Real-time protection") : result;
    }

    public static DefenderOperationResult SetLowCpuPriority(bool enabled)
    {
        string command = "Set-MpPreference -EnableLowCpuPriority " +
                         ToPowerShellBoolean(enabled) + " -ErrorAction Stop";
        var result = RunPreference(command);
        return result.Ok ? Verify(s => s.LowCpuPriority == enabled, "Low CPU priority") : result;
    }

    public static DefenderOperationResult SetScanOnlyIfIdle(bool enabled)
    {
        string command = "Set-MpPreference -ScanOnlyIfIdleEnabled " +
                         ToPowerShellBoolean(enabled) + " -ErrorAction Stop";
        var result = RunPreference(command);
        return result.Ok ? Verify(s => s.ScanOnlyIfIdle == enabled, "Idle-only scan") : result;
    }

    public static DefenderOperationResult SetScheduledScan(bool enabled)
    {
        string day = enabled ? "Everyday" : "Never";
        var result = RunPreference($"Set-MpPreference -ScanScheduleDay {day} -ErrorAction Stop");
        return result.Ok ? Verify(s => s.ScheduledScanEnabled == enabled, "Scheduled scan") : result;
    }

    public static DefenderOperationResult SetCpuLimit(int percent)
    {
        int safePercent = Math.Clamp(percent, 10, 100);
        var result = RunPreference($"Set-MpPreference -ScanAvgCPULoadFactor {safePercent} -ErrorAction Stop");
        return result.Ok ? Verify(s => s.CpuLimit == safePercent, "Scan CPU limit") : result;
    }

    public static DefenderOperationResult ApplyLowImpact()
    {
        var result = RunPreference(
            "Set-MpPreference -EnableLowCpuPriority $true -ScanOnlyIfIdleEnabled $true -ScanAvgCPULoadFactor 30 -ErrorAction Stop");
        return result.Ok
            ? Verify(s => s.LowCpuPriority && s.ScanOnlyIfIdle && s.CpuLimit == 30, "Low-impact profile")
            : result;
    }

    private static DefenderOperationResult Verify(Func<DefenderStatus, bool> predicate, string settingName)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            DefenderStatus status = GetStatus();
            if (status.Available && predicate(status))
                return new DefenderOperationResult(true, settingName);
            Thread.Sleep(250);
        }

        DefenderStatus final = GetStatus();
        return new DefenderOperationResult(false,
            final.Available ? $"{settingName} was not applied by Windows" : final.Error);
    }

    private static DefenderOperationResult RunPreference(string command)
    {
        string script = $$"""
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
try {
    {{command}}
    'OK'
}
catch {
    'ERROR: ' + $_.Exception.Message
    exit 1
}
""";
        var (exitCode, stdout, stderr) = PowerShellRunner.Run(script);
        if (exitCode == 0 && stdout.Contains("OK", StringComparison.Ordinal))
            return new DefenderOperationResult(true, "OK");

        string message = stdout.Trim();
        if (message.StartsWith("ERROR:", StringComparison.Ordinal))
            message = message[6..].Trim();
        if (message.Length == 0) message = stderr.Trim();
        if (message.Length == 0) message = $"PowerShell exit code {exitCode}";
        return new DefenderOperationResult(false, message);
    }


    private static DefenderStatus Unavailable(string error) =>
        new(false, false, false, false, false, false, false, 50, error);

    private static string ToPowerShellBoolean(bool value) => value ? "$true" : "$false";

    private static string ExtractJson(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)].Trim() : string.Empty;
    }

    private sealed class DefenderStatusDto
    {
        public bool Available { get; set; }
        public bool RealtimeEnabled { get; set; }
        public bool ServiceEnabled { get; set; }
        public bool TamperProtected { get; set; }
        public bool LowCpuPriority { get; set; }
        public bool ScanOnlyIfIdle { get; set; }
        public bool ScheduledScanEnabled { get; set; }
        public int CpuLimit { get; set; }
        public string? Error { get; set; }
    }
}
