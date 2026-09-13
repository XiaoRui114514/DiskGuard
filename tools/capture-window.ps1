# 抓取指定进程主窗口的图像（需要与被抓窗口相同或更高权限运行）
param(
    [string]$ProcessName = 'DiskGuard',
    [string]$OutPath = 'ui.png'
)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CaptureNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
'@ -Language CSharp

$process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $process) {
    Write-Error "未找到窗口进程: $ProcessName"
    exit 1
}

$hwnd = $process.MainWindowHandle
$rect = New-Object CaptureNative+RECT
[CaptureNative]::GetWindowRect($hwnd, [ref]$rect) | Out-Null

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap -ArgumentList ([int]$width), ([int]$height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
[CaptureNative]::PrintWindow($hwnd, $hdc, 2) | Out-Null
$graphics.ReleaseHdc($hdc)
$graphics.Dispose()

$full = [System.IO.Path]::GetFullPath($OutPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($full)) | Out-Null
$bitmap.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Host "saved=$full"
