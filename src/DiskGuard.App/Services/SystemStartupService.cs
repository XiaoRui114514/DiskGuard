using System.IO;
using System.Text.Json;
using DiskGuard.Core.Monitoring;

namespace DiskGuard.App.Services;

public sealed record SystemStartupItem(
    string Name, bool Exists, string State, string StartMode, bool DelayedAutoStart,
    int ProcessId = 0, long? MemoryBytes = null, int HostServiceCount = 0, double? IoBytesPerSecond = null);

public sealed record SystemStartupStatus(
    bool Available, IReadOnlyList<SystemStartupItem> Items, string Error);

/// <summary>只读检查与扫盘、更新和安全状态相关的系统服务，不枚举应用自启动。</summary>
public static class SystemStartupService
{
    public static readonly IReadOnlyList<string> ServiceNames = Array.AsReadOnly(
        SystemServiceCatalog.Services.Select(service => service.Name).ToArray());

    public static SystemStartupStatus GetStatus()
    {
        // 名称只来自内置目录；不接受用户输入、通配符或脚本内容。
        string names = string.Join(",", ServiceNames.Select(name => "'" + name + "'"));
        string script = $$"""
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$names = @({{names}})
$all = @(Get-CimInstance -ClassName Win32_Service -Property Name,State,StartMode,DelayedAutoStart,ProcessId -ErrorAction Stop)
$selected = @($all | Where-Object { $names -contains $_.Name })
$hosts = @{}
foreach ($service in $all) {
    if ($service.ProcessId -gt 0) { $hosts[[int]$service.ProcessId]++ }
}
$result = @(foreach ($service in $selected) {
    [pscustomobject]@{
        Name = $service.Name; State = $service.State; StartMode = $service.StartMode
        DelayedAutoStart = [bool]$service.DelayedAutoStart
        ProcessId = [int]$service.ProcessId
        HostServiceCount = [int]$hosts[[int]$service.ProcessId]
    }
})
ConvertTo-Json -InputObject $result -Compress
""";
        var (exitCode, stdout, stderr) = PowerShellRunner.Run(script);
        if (exitCode != 0)
            return new SystemStartupStatus(false, Array.Empty<SystemStartupItem>(),
                string.IsNullOrWhiteSpace(stderr) ? $"PowerShell exit code {exitCode}" : stderr.Trim());

        var status = ParseStatus(stdout);
        if (!status.Available) return status;
        var resources = ProcessResourceSampler.Sample(status.Items.Where(item => item.Exists).Select(item => item.ProcessId));
        return status with
        {
            Items = status.Items.Select(item => resources.TryGetValue(item.ProcessId, out var resource)
                ? item with { MemoryBytes = resource.MemoryBytes, IoBytesPerSecond = resource.IoBytesPerSecond }
                : item).ToArray()
        };
    }

    internal static SystemStartupStatus ParseStatus(string json)
    {
        try
        {
            var services = JsonSerializer.Deserialize<List<ServiceDto>>(json.Trim(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (services == null || !services.Any(service =>
                    ServiceNames.Contains(service.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("Windows returned no system service information");

            var items = ServiceNames.Select(name =>
            {
                var service = services.FirstOrDefault(s =>
                    string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                return new SystemStartupItem(name, service != null,
                    service?.State ?? string.Empty, service?.StartMode ?? string.Empty,
                    service?.DelayedAutoStart ?? false, service?.ProcessId ?? 0,
                    service?.MemoryBytes, service?.HostServiceCount ?? 0, service?.IoBytesPerSecond);
            }).ToArray();
            return new SystemStartupStatus(true, items, string.Empty);
        }
        catch (Exception ex)
        {
            return new SystemStartupStatus(false, Array.Empty<SystemStartupItem>(), ex.Message);
        }
    }

    private sealed class ServiceDto
    {
        public string? Name { get; set; }
        public string? State { get; set; }
        public string? StartMode { get; set; }
        public bool DelayedAutoStart { get; set; }
        public int ProcessId { get; set; }
        public long? MemoryBytes { get; set; }
        public int HostServiceCount { get; set; }
        public double? IoBytesPerSecond { get; set; }
    }
}
