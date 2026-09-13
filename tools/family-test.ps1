# 测试用：一个有窗口的进程（前台程序），它自己启动一个子进程去做磁盘写入。
# 用来验证"保护前台程序"是否覆盖同一程序的子进程。
param(
    [int]$Mb = 800,
    [int]$Seconds = 45
)

$cli = Join-Path $PSScriptRoot '..\src\DiskGuard.Cli\bin\Debug\net8.0-windows\DiskGuard.Cli.exe'
$logPath = Join-Path $env:TEMP 'dgl-child.log'
$child = Start-Process -FilePath $cli -PassThru -WindowStyle Hidden -RedirectStandardOutput $logPath -ArgumentList @(
    'load', '--mb', "$Mb", '--seconds', "$Seconds", '--buffered', "--path=$env:TEMP\dgl-child.tmp"
)

$info = "窗口进程 PID=$PID，子进程 PID=$($child.Id)"
Set-Content -Path (Join-Path $env:TEMP 'dg-family-test-info.txt') -Value $info

Add-Type -AssemblyName PresentationFramework
$window = New-Object System.Windows.Window
$window.Title = 'DG-Family-Test'
$window.Width = 420
$window.Height = 140
$window.WindowStartupLocation = 'Manual'
$window.Left = 80
$window.Top = 80
$text = New-Object System.Windows.Controls.TextBlock
$text.Text = $info
$text.Margin = '16'
$window.Content = $text
[void]$window.ShowDialog()

try { $child.Kill() } catch { }
