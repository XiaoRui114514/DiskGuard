# 测量目标窗口的 UI 线程响应延迟：每 IntervalMs 发一次 WM_NULL，
# 记录往返耗时（= UI 线程消息泵被阻塞的时间），并统计进程 CPU 占用。
param(
    [string]$ProcessName = 'DiskGuard',
    [int]$Seconds = 15,
    [int]$IntervalMs = 200,
    [string]$OutCsv = ''
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UiProbe {
  [DllImport("user32.dll", SetLastError = true)]
  public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
  [DllImport("user32.dll")]
  public static extern bool IsHungAppWindow(IntPtr hWnd);
}
'@ -Language CSharp

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) {
    Write-Error "未找到带窗口的进程: $ProcessName"
    exit 1
}

$hwnd = $proc.MainWindowHandle
$SMTO_ABORTIFHUNG = 0x0002
$samples = New-Object System.Collections.Generic.List[object]
$cpu0 = $proc.TotalProcessorTime
$sw = [Diagnostics.Stopwatch]::StartNew()
$hung = 0

while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $result = [IntPtr]::Zero
    $t = [Diagnostics.Stopwatch]::StartNew()
    $ok = [UiProbe]::SendMessageTimeout($hwnd, 0, [IntPtr]::Zero, [IntPtr]::Zero, $SMTO_ABORTIFHUNG, 4000, [ref]$result)
    $t.Stop()
    $isHung = [UiProbe]::IsHungAppWindow($hwnd)
    if ($isHung) { $hung++ }
    $samples.Add([pscustomobject]@{
        ElapsedMs  = [math]::Round($sw.Elapsed.TotalMilliseconds)
        LatencyMs  = $t.ElapsedMilliseconds
        Delivered  = ($ok -ne [IntPtr]::Zero)
        HungWindow = $isHung
    })

    $sleep = $IntervalMs - $t.ElapsedMilliseconds
    if ($sleep -gt 0) { Start-Sleep -Milliseconds $sleep }
}

$sw.Stop()
$proc.Refresh()
$cpuUsed = ($proc.TotalProcessorTime - $cpu0).TotalSeconds
$cpuPercent = if ($sw.Elapsed.TotalSeconds -gt 0) { $cpuUsed / $sw.Elapsed.TotalSeconds * 100 } else { 0 }

$lat = $samples | ForEach-Object { $_.LatencyMs } | Sort-Object
function Pct([double[]]$values, [double]$p) {
    if ($values.Count -eq 0) { return 0 }
    $idx = [math]::Min($values.Count - 1, [math]::Max(0, [int][math]::Ceiling($p * $values.Count) - 1))
    return $values[$idx]
}

$summary = [pscustomobject]@{
    Process      = "$ProcessName (PID $($proc.Id))"
    DurationSec  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Samples      = $samples.Count
    LostMessages = ($samples | Where-Object { -not $_.Delivered }).Count
    HungSamples  = $hung
    LatencyAvgMs = [math]::Round(($lat | Measure-Object -Average).Average, 1)
    LatencyP50Ms = (Pct $lat 0.50)
    LatencyP95Ms = (Pct $lat 0.95)
    LatencyP99Ms = (Pct $lat 0.99)
    LatencyMaxMs = ($lat | Measure-Object -Maximum).Maximum
    Over200Ms    = ($lat | Where-Object { $_ -ge 200 }).Count
    Over1000Ms   = ($lat | Where-Object { $_ -ge 1000 }).Count
    CpuCoresUsed = [math]::Round($cpuPercent / 100, 3)
    CpuPercent1  = [math]::Round($cpuPercent, 1)
}

if ($OutCsv) {
    $samples | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding UTF8
}

$summary | Format-List | Out-String
