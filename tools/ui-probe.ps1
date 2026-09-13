# UI 交互探针：对目标进程主窗口做真实输入注入（鼠标点击 / 键盘输入）与截图。
# 坐标以窗口左上角为原点（含标题栏），与窗口截图一致。
param(
    [string]$ProcessName = 'DiskGuard',
    [ValidateSet('rect', 'screenshot', 'click', 'click2', 'type', 'key')]
    [string]$Action = 'rect',
    [int]$X = 0,
    [int]$Y = 0,
    [int]$X2 = 0,
    [int]$Y2 = 0,
    [string]$Text = '',
    [string]$Key = '',
    [string]$OutPath = ''
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class UiInput {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr extraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

    public static int InputSize => Marshal.SizeOf(typeof(INPUT));

    public static void Move(int x, int y) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        var input = new INPUT { type = 0 };
        input.u.mi.dx = (int)Math.Round(x * 65535.0 / (sw - 1));
        input.u.mi.dy = (int)Math.Round(y * 65535.0 / (sh - 1));
        input.u.mi.dwFlags = 0x8001;   // MOVE | ABSOLUTE
        SendInput(1, new[] { input }, InputSize);
    }

    public static void Mouse(uint flags) {
        var input = new INPUT { type = 0 };
        input.u.mi.dwFlags = flags;    // 0x0002 左键按下, 0x0004 左键抬起
        SendInput(1, new[] { input }, InputSize);
    }

    public static void Char(char c) {
        var down = new INPUT { type = 1 };
        down.u.ki.wScan = c;
        down.u.ki.dwFlags = 0x0004;    // KEYEVENTF_UNICODE
        var up = down;
        up.u.ki.dwFlags = 0x0004 | 0x0002;   // UNICODE | KEYUP
        SendInput(2, new[] { down, up }, InputSize);
    }

    public static void VirtualKey(ushort vk) {
        var down = new INPUT { type = 1 };
        down.u.ki.wVk = vk;
        var up = down;
        up.u.ki.dwFlags = 0x0002;
        SendInput(2, new[] { down, up }, InputSize);
    }
}
'@ -Language CSharp

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) {
    Write-Error "未找到带窗口的进程: $ProcessName"
    exit 1
}

$hwnd = $proc.MainWindowHandle
$rect = New-Object UiInput+RECT
[void][UiInput]::GetWindowRect($hwnd, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

switch ($Action) {
    'rect' {
        [pscustomobject]@{
            Process = "$ProcessName (PID $($proc.Id))"
            Hwnd    = $hwnd
            Left    = $rect.Left
            Top     = $rect.Top
            Width   = $width
            Height  = $height
            Foreground = [UiInput]::GetForegroundWindow()
        } | Format-List | Out-String
    }
    'screenshot' {
        Add-Type -AssemblyName System.Drawing
        $bitmap = New-Object System.Drawing.Bitmap($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
        $graphics.Dispose()
        $full = [System.IO.Path]::GetFullPath($OutPath)
        $bitmap.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        "saved=$full"
    }
    'click' {
        [void][UiInput]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 300
        [UiInput]::Move(($rect.Left + $X), ($rect.Top + $Y))
        Start-Sleep -Milliseconds 150
        [UiInput]::Mouse(0x0002)
        Start-Sleep -Milliseconds 60
        [UiInput]::Mouse(0x0004)
        "clicked window-offset ($X,$Y)"
    }
    'click2' {
        # 连续两次点击（用于下拉框：先展开，再选条目），中途不重新激活窗口，避免下拉被关掉
        [void][UiInput]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 250
        [UiInput]::Move(($rect.Left + $X), ($rect.Top + $Y))
        Start-Sleep -Milliseconds 150
        [UiInput]::Mouse(0x0002); Start-Sleep -Milliseconds 60; [UiInput]::Mouse(0x0004)
        Start-Sleep -Milliseconds 500
        [UiInput]::Move(($rect.Left + $X2), ($rect.Top + $Y2))
        Start-Sleep -Milliseconds 200
        [UiInput]::Mouse(0x0002); Start-Sleep -Milliseconds 60; [UiInput]::Mouse(0x0004)
        "clicked ($X,$Y) then ($X2,$Y2)"
    }
    'type' {
        foreach ($ch in $Text.ToCharArray()) {
            [UiInput]::Char($ch)
            Start-Sleep -Milliseconds 60
        }
        "typed $($Text.Length) chars"
    }
    'key' {
        $vk = if ($Key -eq 'TAB') { 0x09 } elseif ($Key -eq 'ENTER') { 0x0D } elseif ($Key -eq 'ESC') { 0x1B } elseif ($Key -eq 'BACK') { 0x08 } else { [int]$Key }
        [UiInput]::VirtualKey([ushort]$vk)
        "sent vk=$vk"
    }
}
