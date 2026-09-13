# 测试用：一个"有窗口 + 持续磁盘写入"的进程，用来验证"被限速进程变成前台后应立即解除限速"。
param([int]$BufferMb = 8)

$path = Join-Path $env:TEMP 'dg-fg.tmp'
$bufferSize = $BufferMb * 1024 * 1024

Start-ThreadJob -ScriptBlock {
    param($path, $bufferSize)
    $buffer = New-Object byte[] $bufferSize
    (New-Object Random 12345).NextBytes($buffer)
    $stream = [System.IO.File]::Open($path, 'OpenOrCreate', 'Write', 'ReadWrite')
    try {
        while ($true) {
            $stream.Write($buffer, 0, $buffer.Length)
            $stream.Flush($true)
        }
    } finally {
        $stream.Dispose()
    }
} -ArgumentList $path, $bufferSize | Out-Null

Add-Type -AssemblyName PresentationFramework
$window = New-Object System.Windows.Window
$window.Title = 'DG-Foreground-Test'
$window.Width = 360
$window.Height = 140
$window.WindowStartupLocation = 'Manual'
$window.Left = 60
$window.Top = 60
$text = New-Object System.Windows.Controls.TextBlock
$text.Text = "窗口化磁盘负载（PID $PID）"
$text.Margin = '16'
$window.Content = $text
[void]$window.ShowDialog()
