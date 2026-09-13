using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace DiskGuard.App.Services;

/// <summary>
/// 任务栏托盘图标与右键菜单。
/// 直接调用 Shell_NotifyIcon 实现（不引用 WinForms）：WinForms 会把整套
/// System.Windows.Forms / System.Drawing / VisualBasic 框架带进单文件包（约 20 MB），这里完全用不到。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WM_TRAY_CALLBACK = 0x8000 + 1;   // WM_APP + 1
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_NULL = 0x0000;

    private const uint NIM_ADD = 0x0000;
    private const uint NIM_MODIFY = 0x0001;
    private const uint NIM_DELETE = 0x0002;

    private const uint NIF_MESSAGE = 0x0001;
    private const uint NIF_ICON = 0x0002;
    private const uint NIF_TIP = 0x0004;
    private const uint NIF_INFO = 0x0010;
    private const uint NIIF_INFO = 0x0001;

    private const int IDI_APPLICATION = 32512;
    private const uint TrayIconId = 1;

    private readonly Action<string>? _log;
    private readonly ContextMenu _menu;
    private readonly MenuItem _pauseItem;
    private readonly WndProcDelegate _wndProc;          // 必须保持引用，否则委托会被 GC 回收
    private readonly List<IntPtr> _ownedIcons = new();
    private readonly uint _taskbarCreatedMessage;
    private DispatcherTimer? _retryTimer;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _iconAdded;
    private bool _disposed;

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;
    public event Action? PauseToggleRequested;
    public event Action? ReleaseAllRequested;

    /// <summary>托盘图标是否已成功创建；为 false 时关闭窗口必须直接退出，否则窗口会再也打不开。</summary>
    public bool IsAvailable => _iconAdded;

    public TrayIconService(Action<string>? log = null)
    {
        _log = log;

        _pauseItem = new MenuItem { Header = "暂停监控" };
        _pauseItem.Click += (_, _) => PauseToggleRequested?.Invoke();

        var showItem = new MenuItem { Header = "显示主窗口" };
        showItem.Click += (_, _) => ShowWindowRequested?.Invoke();

        var releaseItem = new MenuItem { Header = "立即还原全部限速" };
        releaseItem.Click += (_, _) => ReleaseAllRequested?.Invoke();

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Items.Add(showItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(releaseItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exitItem);
        _menu.Closed += (_, _) => PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        _wndProc = WndProc;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        try
        {
            _hwnd = CreateMessageWindow();
            _hIcon = LoadTrayIcon();
            AddIcon();
        }
        catch (Exception ex)
        {
            _log?.Invoke("托盘初始化失败：" + ex.Message);
        }

        // 资源管理器尚未就绪时 NIM_ADD 会失败，稍后自动重试
        if (!_iconAdded) StartRetryTimer();
    }

    public void SetPaused(bool paused)
    {
        _pauseItem.Header = paused ? "恢复监控" : "暂停监控";
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_iconAdded || _hwnd == IntPtr.Zero) return;

        try
        {
            var data = CreateIconData();
            data.uFlags = NIF_INFO;
            data.szInfo = Clip(message, 255);
            data.szInfoTitle = Clip(title, 63);
            data.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch
        {
            // 气泡提示失败不影响主流程
        }
    }

    private void StartRetryTimer()
    {
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        int attempts = 0;
        _retryTimer.Tick += (_, _) =>
        {
            if (_disposed || _iconAdded || ++attempts > 10)
            {
                _retryTimer?.Stop();
                return;
            }

            AddIcon();
        };
        _retryTimer.Start();
    }

    private void AddIcon()
    {
        if (_disposed || _hwnd == IntPtr.Zero || _hIcon == IntPtr.Zero) return;

        var data = CreateIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAY_CALLBACK;
        data.hIcon = _hIcon;
        data.szTip = "磁盘守护 DiskGuard";

        if (Shell_NotifyIconW(NIM_ADD, ref data))
        {
            _iconAdded = true;
            _log?.Invoke("托盘图标已就绪（右键可暂停监控 / 还原限速 / 退出）。");
        }
        else
        {
            _iconAdded = false;
            _log?.Invoke("托盘图标创建失败，3 秒后自动重试。");
        }
    }

    private NOTIFYICONDATAW CreateIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 窗口过程是从系统消息循环直接调进来的，这里一旦抛异常会直接结束进程，
        // 所以整段都必须兜住（菜单/气泡等失败只降级，不能把程序带崩）。
        try
        {
            if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                // 资源管理器重启后托盘被重建，需要重新注册图标
                _iconAdded = false;
                AddIcon();
                return IntPtr.Zero;
            }

            if (msg == WM_TRAY_CALLBACK)
            {
                switch ((int)(lParam.ToInt64() & 0xFFFF))
                {
                    case WM_LBUTTONDBLCLK:
                        ShowWindowRequested?.Invoke();
                        return IntPtr.Zero;

                    case WM_RBUTTONUP:
                    case WM_CONTEXTMENU:
                        ShowMenu();
                        return IntPtr.Zero;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("托盘消息处理失败：" + ex.Message);
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        // 先把自己设为前台窗口，菜单在点击别处时才会正常关闭
        SetForegroundWindow(_hwnd);
        _menu.IsOpen = true;
        _menu.Focus();
    }

    private static string Clip(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private IntPtr CreateMessageWindow()
    {
        IntPtr module = GetModuleHandleW(null);
        string className = "DiskGuardTraySink_" + Environment.ProcessId;

        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = module,
            lpszClassName = className
        };

        if (RegisterClassW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClass 失败：" + Marshal.GetLastWin32Error());

        // 顶层（不可见）窗口：消息专用窗口不能成为前台窗口，右键菜单会无法正常关闭
        IntPtr hwnd = CreateWindowExW(0, className, className, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindow 失败：" + Marshal.GetLastWin32Error());

        return hwnd;
    }

    private IntPtr LoadTrayIcon()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) &&
                ExtractIconExW(path, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                if (large != IntPtr.Zero) _ownedIcons.Add(large);
                if (small != IntPtr.Zero) _ownedIcons.Add(small);
                if (small != IntPtr.Zero) return small;
                if (large != IntPtr.Zero) return large;
            }
        }
        catch
        {
            // 回退到系统默认图标
        }

        return LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _retryTimer?.Stop(); } catch { }

        try
        {
            if (_iconAdded)
            {
                var data = CreateIconData();
                Shell_NotifyIconW(NIM_DELETE, ref data);
                _iconAdded = false;
            }
        }
        catch
        {
            // 忽略注销失败
        }

        foreach (IntPtr icon in _ownedIcons)
        {
            try { DestroyIcon(icon); } catch { }
        }
        _ownedIcons.Clear();

        if (_hwnd != IntPtr.Zero)
        {
            try { DestroyWindow(_hwnd); } catch { }
            _hwnd = IntPtr.Zero;
        }
    }

    // ---- Win32 互操作 ----

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
