using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DiskGuard.App.Services;
using DiskGuard.App.ViewModels;
using DiskGuard.Core.Config;
using DiskGuard.Core.Engine;
using DiskGuard.Core.Logging;
using DiskGuard.Core.Monitoring;
using DiskGuard.Core.Util;

namespace DiskGuard.App;

public partial class MainWindow : Window
{
    /// <summary>项目主页：发布到 GitHub 后把仓库地址填在这里，「关于」页就会出现"打开项目主页"按钮。</summary>
    private const string ProjectUrl = "https://github.com/XiaoRui114514/DiskGuard";

    private readonly GuardEngine _engine;
    private readonly AppSettings _settings;
    private readonly AppLogger _logger;
    private readonly TrayIconService _tray;
    private readonly SettingsVm _settingsVm;
    private readonly Dictionary<int, ProcessRowVm> _rowMap = new();
    private bool _exiting;
    private bool _applyingSettings;
    private readonly object _snapshotSync = new();
    private EngineSnapshot? _pendingSnapshot;
    private bool _snapshotQueued;
    private bool _trayPausedShown;

    public ObservableCollection<ProcessRowVm> Rows { get; } = new();
    public ObservableCollection<LogRowVm> Logs { get; } = new();
    public SettingsVm Settings { get; }

    public MainWindow(GuardEngine engine, AppSettings settings, AppLogger logger)
    {
        InitializeComponent();

        _engine = engine;
        _settings = settings;
        _logger = logger;
        _settingsVm = SettingsVm.From(settings);
        Settings = _settingsVm;

        DataContext = this;

        _tray = new TrayIconService(message => _logger.Info(message));
        _tray.ShowWindowRequested += ShowFromTray;
        _tray.PauseToggleRequested += TogglePause;
        _tray.ReleaseAllRequested += () => _engine.ReleaseTarget("托盘菜单：立即还原");
        _tray.ExitRequested += ExitApplication;

        _engine.SnapshotProduced += OnSnapshot;
        _logger.EntryWritten += OnLogEntry;

        string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = "版本 " + version;
        AboutVersionText.Text = $"版本 {version} · MIT 许可 · 单文件（自带 .NET 运行时）";
        ProjectHomeButton.Visibility = ProjectUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivilegeText.Text = ElevationService.IsElevated
            ? "权限：管理员（完整功能）"
            : "权限：普通用户（系统进程无法限速，建议以管理员身份运行）";
        ElevateButton.Visibility = ElevationService.IsElevated ? Visibility.Collapsed : Visibility.Visible;

        foreach (var entry in _logger.Recent)
            AddLog(entry);

        if (settings.StartMinimized && settings.ShowBalloon)
            _tray.ShowBalloon("磁盘守护已在后台运行", "已最小化到托盘并开始监控磁盘占用，右键托盘图标可打开主界面。");
    }

    private void OnSnapshot(EngineSnapshot snapshot)
    {
        // 只保留最新一份快照，并且用 Background 优先级派发：
        // 这样界面刷新永远不会插到键盘/鼠标输入前面，打字不会被刷新挤掉。
        lock (_snapshotSync)
        {
            _pendingSnapshot = snapshot;
            if (_snapshotQueued) return;
            _snapshotQueued = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainSnapshot));
    }

    private void DrainSnapshot()
    {
        EngineSnapshot? snapshot;
        lock (_snapshotSync)
        {
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
            _snapshotQueued = false;
        }

        if (snapshot != null) ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(EngineSnapshot snapshot)
    {
        try
        {
            ApplySnapshotCore(snapshot);
        }
        catch (Exception ex)
        {
            _logger.Error("界面刷新失败：" + ex.Message);
        }
    }

    private void ApplySnapshotCore(EngineSnapshot snapshot)
    {
        BusyText.Text = $"{snapshot.BusyPercent:0}%";
        BusyBar.Value = Math.Clamp(snapshot.BusyPercent, 0, 100);
        DetailText.Text = $"队列 {snapshot.QueueLength:0.00} · 读 {ProcessUtil.FormatRate(snapshot.ReadBytesPerSec)} · 写 {ProcessUtil.FormatRate(snapshot.WriteBytesPerSec)}" +
                          $" · 忙率 {snapshot.TriggerPercent:0}/{snapshot.RecoverPercent:0}%" +
                          $" · 磁盘占用 {snapshot.TriggerOccupancyPercent:0}→{snapshot.TargetOccupancyPercent:0}%";

        StateText.Text = snapshot.StateText;
        StateBadge.Background = snapshot.StateKind switch
        {
            "throttled" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2)),
            "watch" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7)),
            "paused" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0xE7, 0xEB)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xF1, 0xFF))
        };
        StateText.Foreground = snapshot.StateKind switch
        {
            "throttled" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C)),
            "watch" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09)),
            "paused" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4B, 0x55, 0x63)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x4E, 0xD8))
        };

        if (snapshot.ActivePid > 0)
        {
            string level = snapshot.ActiveLevel switch
            {
                3 => "强力模式（间歇挂起）",
                2 => $"吞吐上限 {snapshot.ActiveCapBytesPerSec / 1024.0 / 1024.0:0.#} MB/s",
                _ => "低优先级"
            };
            TargetText.Text = $"正在限速：{snapshot.ActiveName} (PID {snapshot.ActivePid})\n" +
                              $"方式：{level}，磁盘占用 {snapshot.ActiveOccupancyPercent:0.#}%（目标 {snapshot.TargetOccupancyPercent:0.#}%）\n" +
                              $"当前速率 {ProcessUtil.FormatRate(snapshot.ActiveRateBytesPerSec)}";
        }
        else
        {
            TargetText.Text = "当前未限速";
        }

        PauseButton.Content = snapshot.Paused ? "恢复监控" : "暂停监控";
        if (_trayPausedShown != snapshot.Paused)
        {
            _trayPausedShown = snapshot.Paused;
            _tray.SetPaused(snapshot.Paused);
        }

        // 更新磁盘下拉框
        if (snapshot.Disks.Count > 0)
        {
            var current = DiskCombo.SelectedItem as DiskStatus;
            if (current == null || DiskCombo.Items.Count != snapshot.Disks.Count)
            {
                _applyingSettings = true;
                DiskCombo.ItemsSource = snapshot.Disks;
                DiskCombo.SelectedItem = snapshot.Disks.FirstOrDefault(d => d.DiskNumber == snapshot.DiskNumber) ?? snapshot.Disks.FirstOrDefault();
                _applyingSettings = false;
            }
        }

        // 更新进程行（原地更新，避免闪烁）
        var seen = new HashSet<int>();
        int index = 0;
        foreach (var row in snapshot.Rows)
        {
            seen.Add(row.Pid);
            if (!_rowMap.TryGetValue(row.Pid, out var vm))
            {
                vm = new ProcessRowVm { Pid = row.Pid, Name = row.Name };
                _rowMap[row.Pid] = vm;
                Rows.Insert(Math.Min(index, Rows.Count), vm);
            }

            vm.Name = row.Name;
            vm.OccupancyPercent = row.OccupancyPercent;
            vm.ReadBytesPerSec = row.ReadBytesPerSec;
            vm.WriteBytesPerSec = row.WriteBytesPerSec;
            vm.Tag = row.Tag;

            int currentIndex = Rows.IndexOf(vm);
            if (currentIndex >= 0 && currentIndex != index && index < Rows.Count)
                Rows.Move(currentIndex, index);

            index++;
        }

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Rows[i].Pid))
            {
                _rowMap.Remove(Rows[i].Pid);
                Rows.RemoveAt(i);
            }
        }
    }

    private void OnLogEntry(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() => AddLog(entry));
    }

    private void AddLog(LogEntry entry)
    {
        Logs.Insert(0, new LogRowVm { Time = entry.TimeText, Level = entry.LevelText, Message = entry.Message });
        while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
    }

    private void OnDiskChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        if (DiskCombo.SelectedItem is not DiskStatus disk) return;
        if (disk.DiskNumber < 0) return;
        if (_settings.DiskNumber == disk.DiskNumber) return;

        _settings.DiskNumber = disk.DiskNumber;
        _settings.Save(AppPaths.SettingsFile);
        _engine.UpdateSettings(_settings);
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (_engine.Paused) _engine.Resume();
        else _engine.Pause();
    }

    private void OnReleaseClicked(object sender, RoutedEventArgs e)
    {
        _engine.ReleaseTarget("手动解除");
    }

    private void OnManualThrottleClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessRowVm row)
        {
            System.Windows.MessageBox.Show("请先在列表中选择一个进程。", "磁盘守护", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_engine.ManualThrottle(row.Pid, row.Name))
            System.Windows.MessageBox.Show($"无法限速 {row.Name}（可能权限不足或属于系统关键进程）。", "磁盘守护",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnWhitelistAddClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessRowVm row) return;
        string name = row.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? row.Name : row.Name + ".exe";
        if (_settings.Whitelist.Any(w => w.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        _settings.Whitelist.Add(name);
        _settings.Save(AppPaths.SettingsFile);
        _settingsVm.ReloadFrom(_settings);
        _logger.Info($"已加入保护名单：{name}");
    }

    private void OnWhitelistRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessRowVm row) return;
        string name = row.Name;
        int removed = _settings.Whitelist.RemoveAll(w =>
            w.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            w.Equals(name + ".exe", StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            _settings.Save(AppPaths.SettingsFile);
            _settingsVm.ReloadFrom(_settings);
            _logger.Info($"已移出保护名单：{name}");
        }
    }

    private void OnSaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        _settingsVm.ApplyTo(_settings);
        _settings.Save(AppPaths.SettingsFile);
        _engine.UpdateSettings(_settings);
        _logger.WriteToFile = _settings.WriteLogFile;
        _settingsVm.ReloadFrom(_settings);
        _logger.Info("设置已保存并生效。");
        System.Windows.MessageBox.Show("设置已保存并立即生效。", "磁盘守护", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnResetSettingsClicked(object sender, RoutedEventArgs e)
    {
        int disk = _settings.DiskNumber;
        var defaults = new AppSettings { DiskNumber = disk };
        _settingsVm.ReloadFrom(defaults);
        _settingsVm.ApplyTo(_settings);
        _settings.Save(AppPaths.SettingsFile);
        _engine.UpdateSettings(_settings);
        _logger.WriteToFile = _settings.WriteLogFile;
        _logger.Info("已恢复默认设置。");
    }

    private async void OnAutoStartClicked(object sender, RoutedEventArgs e)
    {
        bool wanted = AutoStartCheck.IsChecked == true;

        if (wanted && !ElevationService.IsElevated)
        {
            AutoStartCheck.IsChecked = false;
            System.Windows.MessageBox.Show("开启开机自动启动需要管理员权限（计划任务）。点击「以管理员身份重启」后即可开启。",
                "磁盘守护", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // schtasks.exe 是同步等待的子进程，放到后台线程，避免点一下卡住界面
        AutoStartCheck.IsEnabled = false;
        var result = await System.Threading.Tasks.Task.Run(() => wanted
            ? AutoStartService.Enable(AutoStartService.ExecutablePath)
            : AutoStartService.Disable()).ConfigureAwait(true);
        AutoStartCheck.IsEnabled = true;

        _settings.AutoStart = wanted && result.Ok;
        _settings.Save(AppPaths.SettingsFile);
        _logger.Info(result.Message);

        if (!result.Ok)
        {
            AutoStartCheck.IsChecked = _settings.AutoStart;
            System.Windows.MessageBox.Show(result.Message, "磁盘守护", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        Logs.Clear();
        _logger.Clear();
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开目录失败：" + ex.Message, "磁盘守护", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopyDouyinClicked(object sender, RoutedEventArgs e) => CopyAccount(DouyinText.Text, "抖音号");

    private void OnCopyXiaohongshuClicked(object sender, RoutedEventArgs e) => CopyAccount(XiaohongshuText.Text, "小红书号");

    private void CopyAccount(string text, string label)
    {
        try
        {
            Clipboard.SetText(text);
            AboutCopyHint.Text = $"已复制{label}：{text}";
            _logger.Info($"已复制{label}：{text}");
        }
        catch (Exception ex)
        {
            AboutCopyHint.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OnOpenProjectHomeClicked(object sender, RoutedEventArgs e)
    {
        if (ProjectUrl.Length == 0) return;

        try
        {
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开项目主页失败：" + ex.Message, "磁盘守护", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnElevateClicked(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RestartElevated()) ExitApplication();
    }

    private void ShowFromTray()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (_exiting) return;

        // 托盘不可用时隐藏窗口会让程序再也打不开，此时直接退出更安全
        if (!_tray.IsAvailable)
        {
            _logger.Warn("托盘图标不可用，关闭窗口时直接退出程序。");
            ExitApplication();
            return;
        }

        e.Cancel = true;
        Hide();
        if (_settings.ShowBalloon)
            _tray.ShowBalloon("磁盘守护仍在运行", "程序已最小化到托盘，继续监控磁盘占用。");
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        // 从托盘菜单点击进来时，要等菜单事件处理完再拆托盘与窗口
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShutdownCore));
    }

    private void ShutdownCore()
    {
        try { _engine.ReleaseTarget("退出程序"); } catch { }
        try { _engine.Stop(); } catch { }
        try { _tray.Dispose(); } catch { }
        try { _logger.Dispose(); } catch { }   // 把队列里的日志写完
        System.Windows.Application.Current.Shutdown();
    }
}
