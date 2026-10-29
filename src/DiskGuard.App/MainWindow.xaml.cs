using System.Text.Json;
using System.IO.Compression;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DiskGuard.App.Services;
using DiskGuard.App.Localization;
using DiskGuard.App.ViewModels;
using DiskGuard.Core.Config;
using DiskGuard.Core.Engine;
using DiskGuard.Core.Localization;
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
    private readonly DeveloperRecorder _developerRecorder;
    private readonly DispatcherTimer _developerHeartbeat;
    private long _lastHeartbeatAt = Stopwatch.GetTimestamp();
    private DeveloperLogWindow? _developerLogWindow;
    private readonly TrayIconService _tray;
    private readonly SettingsVm _settingsVm;
    private readonly Dictionary<string, CheckBox> _serviceToggles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceProbe> _lastServiceStates = new(StringComparer.OrdinalIgnoreCase);
    private DefenderStatus? _lastDefenderStatus;
    private ScheduledTaskStatus _lastScheduledTaskStatus;
    private bool _systemControlsBusy;
    private bool _updatingSystemControls;
    private readonly Dictionary<int, ProcessRowVm> _rowMap = new();
    private bool _exiting;
    private bool _identityPause;
    private int _diskSelectionRevision;
    private EngineSnapshot? _lastSnapshot;
    private readonly Dictionary<int, string> _diskIdentities = new();
    private bool _applyingSettings;
    private readonly object _snapshotSync = new();
    private EngineSnapshot? _pendingSnapshot;
    private bool _snapshotQueued;
    private bool _trayPausedShown;
    private string _version = "1.0.0";

    public ObservableCollection<ProcessRowVm> Rows { get; } = new();
    public ObservableCollection<LogRowVm> Logs { get; } = new();
    public SettingsVm Settings { get; }

    public MainWindow(GuardEngine engine, AppSettings settings, AppLogger logger, DeveloperRecorder developerRecorder)
    {
        InitializeComponent();

        _engine = engine;
        _settings = settings;
        if (settings.SelectedDiskIdentity.Length > 0) { _identityPause = true; _engine.Pause(); }
        _logger = logger;
        _developerRecorder = developerRecorder;
        _settingsVm = SettingsVm.From(settings);
        Settings = _settingsVm;

        DataContext = this;
        Loaded += async (_, _) => await LoadDiskIdentitiesAsync();

        _serviceToggles["SysMain"] = StopSysMainCheck;
        _serviceToggles["WSearch"] = StopWSearchCheck;
        _serviceToggles["DoSvc"] = StopDoSvcCheck;
        _serviceToggles["wuauserv"] = StopWuauservCheck;
        _serviceToggles["BITS"] = StopBitsCheck;
        UpdateSystemControlAvailability();

        // 设置页还没打开也要执行：开机计划任务从托盘启动时同样会应用记住的暂停项。
        _ = RefreshSystemControlsAsync(applyRememberedPauses: true);

        _tray = new TrayIconService(message => _logger.Info(message));
        _tray.ShowWindowRequested += ShowFromTray;
        _tray.PauseToggleRequested += TogglePause;
        _tray.ReleaseAllRequested += () => _engine.ReleaseTarget(Loc.T(LK.ReasonTrayReleaseAll));
        _tray.ExitRequested += ExitApplication;

        _engine.SnapshotProduced += OnSnapshot;
        _logger.EntryWritten += OnLogEntry;

        _version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        ProjectHomeButton.Visibility = ProjectUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ElevateButton.Visibility = ElevationService.IsElevated ? Visibility.Collapsed : Visibility.Visible;

        ApplyLanguage(Loc.Current);

        _developerHeartbeat = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _developerHeartbeat.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            _developerRecorder.RecordUiHeartbeat(Stopwatch.GetElapsedTime(_lastHeartbeatAt, now).TotalMilliseconds - 1000, IsVisible);
            _lastHeartbeatAt = now;
        };
        _developerHeartbeat.Start();

        if (settings.StartMinimized && settings.ShowBalloon)
            _tray.ShowBalloon(Loc.T(LK.BalloonStartedTitle), Loc.T(LK.BalloonStartedBody));
    }

    /// <summary>应用界面语言：窗口标题、字体、排版方向（阿拉伯语从右往左）、状态栏文本与日志列表。</summary>
    private void ApplyLanguage(AppLanguage language)
    {
        Title = Loc.T(LK.AppTitle);
        UiLanguage.Apply(this, language);
        _tray.ApplyLanguage();

        VersionText.Text = Loc.F(LK.VersionShortFormat, _version);
        AboutVersionText.Text = Loc.F(LK.VersionFullFormat, _version);
        PrivilegeText.Text = Loc.T(ElevationService.IsElevated ? LK.PrivilegeAdmin : LK.PrivilegeUser);
        RefreshSystemControlTexts();
        RefreshSystemControlTexts();

        // 磁盘下拉框的显示名（“磁盘 0 (C: D:)”）是按语言现算的，清空后由下一次快照重建
        DiskCombo.ItemsSource = null;

        // 日志行的“级别”是按当前语言渲染的，切语言后重建一次列表
        RebuildLogList();
        foreach (var row in _rowMap.Values) row.RefreshDisplayTexts();
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
        long refreshStarted = Stopwatch.GetTimestamp();
        try
        {
            ApplySnapshotCore(snapshot);
        }
        catch (Exception ex)
        {
            _logger.Error(Loc.F(LK.LogUiRefreshFailedFormat, ex.Message));
        }
        finally
        {
            _developerRecorder.RecordUiRefresh(Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds);
        }
    }

    private void ApplySnapshotCore(EngineSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        SourceDetailsText.Text = (snapshot.IoPrecise ? Loc.T(LK.SourcePrecise) : Loc.T(LK.SourceApproximate)) + " · " + snapshot.IoMode;
        SourceDetailsText.ToolTip = snapshot.IoHealthError;

        BusyText.Text = Loc.Value($"{snapshot.BusyPercent:0}%");
        BusyBar.Value = Math.Clamp(snapshot.BusyPercent, 0, 100);
        DetailText.Text = Loc.F(LK.DetailFormat,
            snapshot.QueueLength, Loc.Value(ProcessUtil.FormatRate(snapshot.ReadBytesPerSec)),
            Loc.Value(ProcessUtil.FormatRate(snapshot.WriteBytesPerSec)),
            snapshot.TriggerPercent, snapshot.RecoverPercent,
            snapshot.TriggerOccupancyPercent, snapshot.TargetOccupancyPercent,
            snapshot.LatencyMs >= 0 ? $"{snapshot.LatencyMs:0.#} ms" : "-");

        string systemRate = Loc.Value(ProcessUtil.FormatRate(snapshot.SystemRateBytesPerSec));
        if (snapshot.StartupAgeSeconds < 900 && snapshot.BusyPercent >= snapshot.TriggerPercent)
        {
            SystemDiagnosisText.Text = Loc.F(LK.SystemDiagnosisStartupFormat, snapshot.StartupAgeSeconds / 60.0);
            SystemDiagnosisText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E));
        }
        else if (snapshot.SystemOccupancyPercent >= 5 || snapshot.SystemCpuPercent >= 5)
        {
            SystemDiagnosisText.Text = Loc.F(LK.SystemDiagnosisActiveFormat,
                snapshot.SystemOccupancyPercent, systemRate, snapshot.SystemCpuPercent);
            SystemDiagnosisText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C));
        }
        else
        {
            SystemDiagnosisText.Text = Loc.T(LK.SystemDiagnosisIdle);
            SystemDiagnosisText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80));
        }

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
                3 => Loc.T(LK.ThrottleLevelSuspend),
                2 => snapshot.ActiveCapIops > 0
                    ? Loc.F(LK.ThrottleLevelCapIopsFormat,
                        snapshot.ActiveCapBytesPerSec / 1024.0 / 1024.0, snapshot.ActiveCapIops)
                    : Loc.F(LK.ThrottleLevelCapFormat, snapshot.ActiveCapBytesPerSec / 1024.0 / 1024.0),
                _ => Loc.T(LK.ThrottleLevelPriority)
            };
            TargetText.Text = Loc.F(LK.TargetThrottledFormat,
                snapshot.ActiveName, snapshot.ActivePid, level,
                snapshot.ActiveOccupancyPercent, snapshot.TargetOccupancyPercent,
                Loc.Value(ProcessUtil.FormatRate(snapshot.ActiveRateBytesPerSec)));
        }
        else
        {
            TargetText.Text = Loc.T(LK.TargetNone);
        }

        PauseButton.Content = Loc.T(snapshot.Paused ? LK.BtnResumeMonitor : LK.BtnPauseMonitor);
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
                DiskCombo.SelectedItem = snapshot.Disks.FirstOrDefault(d => d.DiskNumber == snapshot.DiskNumber);
                _applyingSettings = false;
            }
        }

        // 设置页输入时不必每秒重排进程表；窗口隐藏到托盘时也暂停列表绑定更新。
        // 其他卡片仍正常刷新，重新显示进程页后下一份快照会补齐最新排行。
        if (!ProcessGrid.IsVisible) return;

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
        Logs.Insert(0, new LogRowVm { Time = entry.TimeText, Level = entry.LevelText, Message = SanitizeLogMessage(entry.Message) });
        while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
    }

    /// <summary>
    /// WPF 文本排版遇到超长、含控制字符或损坏的代理项时可能反复抛异常。
    /// 日志只是用于排查，限制单行长度并清理异常字符，避免打开日志页触发排版风暴。
    /// </summary>
    private static string SanitizeLogMessage(string message)
    {
        const int maxLength = 1200;
        if (string.IsNullOrEmpty(message)) return string.Empty;

        var builder = new StringBuilder(Math.Min(message.Length, maxLength));
        bool previousWasSpace = false;
        for (int i = 0; i < message.Length && builder.Length < maxLength; i++)
        {
            char c = message[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < message.Length && char.IsLowSurrogate(message[i + 1]))
                {
                    builder.Append(c);
                    builder.Append(message[++i]);
                }
                else
                {
                    builder.Append('\uFFFD');
                }
                previousWasSpace = false;
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                builder.Append('\uFFFD');
                previousWasSpace = false;
                continue;
            }

            if (c is '\r' or '\n' or '\t') c = ' ';
            if (char.IsControl(c) || (char.IsWhiteSpace(c) && c != ' ')) c = ' ';
            if (c == ' ')
            {
                if (previousWasSpace) continue;
                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }

            builder.Append(c);
        }

        if (message.Length > maxLength) builder.Append('…');
        return builder.ToString().Trim();
    }

    /// <summary>语言切换后按新语言重新渲染日志列表（消息本身保持写入时的语言）。</summary>
    private void RebuildLogList()
    {
        Logs.Clear();
        foreach (var entry in _logger.Recent) AddLog(entry);
    }

    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        if (LanguageCombo.SelectedValue is not string code) return;

        AppLanguage language = Loc.FromCode(code);
        string normalized = Loc.Code(language);
        _settingsVm.Language = normalized;
        if (Loc.Current == language) return;

        Loc.SetLanguage(language);
        _settings.Language = normalized;
        _settings.Save(AppPaths.SettingsFile);
        ApplyLanguage(language);
        _logger.Info(Loc.F(LK.LogLanguageChangedFormat, Loc.NativeName(language)));
    }

    private async Task<bool> ReadDiskIdentitiesAsync()
    {
        var result = await Task.Run(() => PowerShellRunner.Run("[Console]::OutputEncoding=[Text.Encoding]::UTF8; @(Get-Disk | Select-Object Number,UniqueId) | ConvertTo-Json -Compress", 15000));
        if (result.ExitCode != 0) { _diskIdentities.Clear(); DiskCombo.ToolTip = Loc.T(LK.DiskIdentityUnavailable); return false; }
        try
        {
            using var json = JsonDocument.Parse(result.Stdout);
            var rows = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : new[] { json.RootElement };
            var entries = rows.Select(r => (Number:r.GetProperty("Number").GetInt32(), Id:r.GetProperty("UniqueId").GetString()?.Trim() ?? "")).Where(r => r.Id.Length > 0).ToArray();
            _diskIdentities.Clear();
            foreach (var row in entries.Where(r => entries.Count(x => x.Id == r.Id) == 1)) _diskIdentities[row.Number] = row.Id;
            return true;
        }
        catch (Exception ex) { _diskIdentities.Clear(); DiskCombo.ToolTip = Loc.T(LK.DiskIdentityUnavailable); _logger.Warn(ex.Message); return false; }
    }

    private async Task LoadDiskIdentitiesAsync()
    {
        if (!await ReadDiskIdentitiesAsync()) return;
        try
        {
            var saved = _settings.SelectedDiskIdentity;
            var matching = _diskIdentities.Where(x => x.Value == saved).ToArray();
            if (saved.Length > 0 && matching.Length == 0)
            {
                _engine.Pause();
                _settings.DiskNumber = -1;
                _engine.UpdateSettings(_settings);
                DiskCombo.ToolTip = Loc.T(LK.DiskIdentityUnavailable); return;
            }
            if (matching.Length == 1) _settings.DiskNumber = matching[0].Key;
            if (saved.Length == 0) SaveDiskPolicy();
            SelectDiskPolicy();
            _settingsVm.ReloadFrom(_settings);
            _settings.Save(AppPaths.SettingsFile);
            _engine.UpdateSettings(_settings);
            if (_identityPause) { _identityPause = false; _engine.Resume(); }
        }
        catch (Exception ex) { _logger.Warn(ex.Message); }
    }

    private void SaveDiskPolicy()
    {
        string id = _settings.SelectedDiskIdentity;
        if (id.Length == 0)
        {
            if (!_diskIdentities.TryGetValue(_settings.DiskNumber, out var resolved) || string.IsNullOrEmpty(resolved)) return;
            id = resolved;
        }
        _settings.DiskProfiles[id] = DiskPolicy.Capture(_settings);
        _settings.SelectedDiskIdentity = id;
    }

    private void SelectDiskPolicy()
    {
        if (!_diskIdentities.TryGetValue(_settings.DiskNumber, out var id)) { _settings.SelectedDiskIdentity = string.Empty; return; }
        if (_settings.DiskProfiles.TryGetValue(id, out var policy) && policy != null) policy.Apply(_settings);
        else DiskPolicy.Capture(new AppSettings()).Apply(_settings);
        _settings.SelectedDiskIdentity = id;
    }

    private void OnRetrySourceClicked(object sender, RoutedEventArgs e)
    {
        SourceDetailsText.ToolTip = _engine.RequestPreciseStatistics() ? Loc.T(LK.SourceRetryQueued) : Loc.T(LK.SourceRetryCooldown);
    }

    private async void OnExportDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await _developerRecorder.FlushAsync();
            var files = new[] { AppPaths.LogDirectory, AppPaths.DeveloperLogDirectory }.Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d)).Select(p => new FileInfo(p))
                .Where(f => f.Length <= 10 * 1024 * 1024 && f.Extension != ".zip" && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).OrderByDescending(f => f.LastWriteTimeUtc).Take(20).ToArray();
            var preview = Loc.T(LK.DiagnosticsPreview) + "\n\n" + string.Join("\n", files.Select(f => f.Directory!.Name + "/" + f.Name));
            if (System.Windows.MessageBox.Show(preview, Loc.T(LK.BtnExportDiagnostics), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "ZIP|*.zip", FileName = "DiskGuard-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
            if (dialog.ShowDialog() != true) return;
            var settingsJson = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            var snapshotJson = JsonSerializer.Serialize(new { version = _version, snapshot = _lastSnapshot });
            await Task.Run(() =>
            {
                using var archiveFile = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
                using var zip = new ZipArchive(archiveFile, ZipArchiveMode.Create);
                using (var writer = new StreamWriter(zip.CreateEntry("settings.json").Open())) writer.Write(settingsJson);
                using (var writer = new StreamWriter(zip.CreateEntry("snapshot.json").Open())) writer.Write(snapshotJson);
                foreach (var file in files)
                {
                    using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = zip.CreateEntry(file.Directory!.Name + "/" + file.Name).Open();
                    var buffer = new byte[8192]; long left = 10 * 1024 * 1024;
                    int read;
                    while (left > 0 && (read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0) { output.Write(buffer, 0, read); left -= read; }
                }
            });
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, Loc.T(LK.BtnExportDiagnostics), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void OnDiskChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        if (DiskCombo.SelectedItem is not DiskStatus disk) return;
        if (disk.DiskNumber < 0) return;
        if (_settings.DiskNumber == disk.DiskNumber) return;

        int revision = ++_diskSelectionRevision;
        bool identified = await ReadDiskIdentitiesAsync();
        if (revision != _diskSelectionRevision) return;
        SaveDiskPolicy();
        _engine.ReleaseTarget(Loc.T(LK.StateMonitoring));
        _settings.DiskNumber = disk.DiskNumber;
        if (identified) SelectDiskPolicy(); else _settings.SelectedDiskIdentity = string.Empty;
        _settingsVm.ReloadFrom(_settings);
        _settings.Save(AppPaths.SettingsFile);
        _engine.UpdateSettings(_settings);
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        _identityPause = false;
        if (_engine.Paused) _engine.Resume();
        else _engine.Pause();
    }

    private void OnReleaseClicked(object sender, RoutedEventArgs e)
    {
        _engine.ReleaseTarget(Loc.T(LK.ReasonManualRelease));
    }

    private void OnManualThrottleClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessRowVm row)
        {
            System.Windows.MessageBox.Show(Loc.T(LK.MsgSelectProcessFirst), Loc.T(LK.AppTitle),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_engine.ManualThrottle(row.Pid, row.Name))
            System.Windows.MessageBox.Show(Loc.F(LK.MsgCannotThrottleFormat, row.Name), Loc.T(LK.AppTitle),
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
        _logger.Info(Loc.F(LK.LogWhitelistAddedFormat, name));
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
            _logger.Info(Loc.F(LK.LogWhitelistRemovedFormat, name));
        }
    }

    private void OnSaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        _settingsVm.ApplyTo(_settings);
        SaveDiskPolicy();
        _settings.Save(AppPaths.SettingsFile);
        _engine.UpdateSettings(_settings);
        _logger.WriteToFile = _settings.WriteLogFile;
        _developerRecorder.Configure(_settings);
        _settingsVm.ReloadFrom(_settings);
        _logger.Info(Loc.T(LK.LogSettingsSaved));
        System.Windows.MessageBox.Show(Loc.T(LK.MsgSettingsSaved), Loc.T(LK.AppTitle),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnResetSettingsClicked(object sender, RoutedEventArgs e)
    {
        int disk = _settings.DiskNumber;
        var defaults = new AppSettings { DiskNumber = disk };
        _settingsVm.ReloadFrom(defaults);
        _settings.PausedServicesOnStartup.Clear();
        _settingsVm.ApplyTo(_settings);
        SaveDiskPolicy();
        _settings.Save(AppPaths.SettingsFile);
        _engine.UpdateSettings(_settings);
        _logger.WriteToFile = _settings.WriteLogFile;
        _developerRecorder.Configure(_settings);
        _logger.Info(Loc.T(LK.LogDefaultsRestored));
    }

    private void OnDeveloperModeClicked(object sender, RoutedEventArgs e)
    {
        _settings.DeveloperMode = DeveloperModeCheck.IsChecked == true;
        _settingsVm.DeveloperMode = _settings.DeveloperMode;
        _settings.Save(AppPaths.SettingsFile);
        _developerRecorder.Configure(_settings);
    }

    private void OnViewDeveloperLogClicked(object sender, RoutedEventArgs e)
    {
        if (_developerLogWindow != null)
        {
            _developerLogWindow.Activate();
            return;
        }
        _developerLogWindow = new DeveloperLogWindow(_developerRecorder) { Owner = this };
        _developerLogWindow.Closed += (_, _) => _developerLogWindow = null;
        _developerLogWindow.Show();
    }

    private async void OnOpenDeveloperLogFolderClicked(object sender, RoutedEventArgs e)
    {
        await _developerRecorder.FlushAsync();
        try
        {
            if (!Directory.Exists(AppPaths.DeveloperLogDirectory)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DeveloperLogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Loc.F(LK.MsgOpenDirFailedFormat, ex.Message), Loc.T(LK.AppTitle),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRefreshSystemControlsClicked(object sender, RoutedEventArgs e)
    {
        await RefreshSystemControlsAsync(applyRememberedPauses: false);
    }

    private async Task RefreshSystemControlsAsync(bool applyRememberedPauses)
    {
        if (_systemControlsBusy) return;

        _systemControlsBusy = true;
        UpdateSystemControlAvailability();
        SystemControlsStatusText.Text = Loc.T(LK.StatusRefreshing);
        DefenderStatusText.Text = Loc.T(LK.StatusRefreshing);

        try
        {
            var applyFailures = new List<(string Name, string Message)>();
            if (applyRememberedPauses && ElevationService.IsElevated)
            {
                var remembered = _settings.PausedServicesOnStartup
                    .Where(ServiceControlService.IsPausable)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (remembered.Length > 0)
                {
                    applyFailures = await Task.Run(() => remembered
                        .Select(name => (Name: name, Result: ServiceControlService.SetRunning(name, false)))
                        .Where(x => !x.Result.Ok)
                        .Select(x => (x.Name, x.Result.Message))
                        .ToList());
                }
            }

            var data = await Task.Run(() => (
                Services: ServiceControlService.QueryAll(),
                Defender: DefenderService.GetStatus(),
                DriveTask: ScheduledTaskService.GetStatus()));

            ApplyServiceStates(data.Services);
            ApplyDefenderStatus(data.Defender);
            ApplyScheduledTaskStatus(data.DriveTask);

            if (!data.Defender.Available)
                _logger.Warn("Defender status: " + data.Defender.Error);
            if (!data.DriveTask.Available)
                _logger.Warn("ScheduledDefrag status: " + data.DriveTask.Error);

            foreach (var failure in applyFailures)
                _logger.Warn(Loc.F(LK.MsgServiceToggleFailedFormat, failure.Name, failure.Message));
        }
        catch (Exception ex)
        {
            string message = Loc.F(LK.StatusRefreshFailedFormat, ex.Message);
            SystemControlsStatusText.Text = message;
            DefenderStatusText.Text = message;
            DefenderStatusText.Foreground = (Brush)FindResource("DangerBrush");
            _logger.Warn(message);
        }
        finally
        {
            _systemControlsBusy = false;
            UpdateSystemControlAvailability();
        }
    }

    private void ApplyServiceStates(IReadOnlyDictionary<string, ServiceProbe> states)
    {
        _lastServiceStates.Clear();
        foreach (var pair in states) _lastServiceStates[pair.Key] = pair.Value;

        _updatingSystemControls = true;
        try
        {
            foreach (var pair in _serviceToggles)
            {
                pair.Value.IsChecked = states.TryGetValue(pair.Key, out ServiceProbe state) && state.Running;
            }
        }
        finally
        {
            _updatingSystemControls = false;
        }

        RefreshSystemControlTexts();
    }

    private void ApplyDefenderStatus(DefenderStatus status)
    {
        _lastDefenderStatus = status;
        _updatingSystemControls = true;
        try
        {
            DefenderRealtimeCheck.IsChecked = status.RealtimeEnabled;
            DefenderLowCpuCheck.IsChecked = status.LowCpuPriority;
            DefenderIdleScanCheck.IsChecked = status.ScanOnlyIfIdle;
            DefenderScheduledScanCheck.IsChecked = status.ScheduledScanEnabled;

            int[] limits = { 20, 30, 50, 70, 100 };
            int bestIndex = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < limits.Length; i++)
            {
                int distance = Math.Abs(limits[i] - status.CpuLimit);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }
            DefenderCpuLimitCombo.SelectedIndex = bestIndex;
        }
        finally
        {
            _updatingSystemControls = false;
        }

        RefreshSystemControlTexts();
    }

    private void ApplyScheduledTaskStatus(ScheduledTaskStatus status)
    {
        _lastScheduledTaskStatus = status;
        _updatingSystemControls = true;
        try
        {
            ScheduledDefragCheck.IsChecked = status.Enabled;
        }
        finally
        {
            _updatingSystemControls = false;
        }
        RefreshSystemControlTexts();
    }

    private void RefreshSystemControlTexts()
    {
        ScheduledDefragCheck.ToolTip = _lastScheduledTaskStatus.Available
            ? null
            : Loc.T(LK.MsgScheduledTaskUnavailable) + (string.IsNullOrWhiteSpace(_lastScheduledTaskStatus.Error) ? string.Empty : Environment.NewLine + _lastScheduledTaskStatus.Error);
        if (_lastServiceStates.Count == 0)
        {
            SystemControlsStatusText.Text = "--";
        }
        else
        {
            int running = _lastServiceStates.Values.Count(x => x.Exists && x.Running);
            int stopped = _lastServiceStates.Values.Count(x => x.Exists && !x.Running);
            SystemControlsStatusText.Text = Loc.F(LK.ServiceSummaryFormat, running, stopped);
            SystemControlsStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }

        if (_lastDefenderStatus is not { } defender)
        {
            DefenderStatusText.Text = "--";
            return;
        }

        if (!defender.Available)
        {
            DefenderStatusText.Text = Loc.F(LK.DefenderUnavailableFormat, defender.Error);
            DefenderStatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        DefenderStatusText.Text = Loc.F(
            LK.DefenderStatusFormat,
            Loc.T(defender.RealtimeEnabled ? LK.CommonOn : LK.CommonOff),
            Loc.T(defender.ServiceEnabled ? LK.CommonOn : LK.CommonOff),
            Loc.T(defender.TamperProtected ? LK.CommonOn : LK.CommonOff));
        DefenderStatusText.Foreground = (Brush)FindResource(defender.RealtimeEnabled ? "SuccessBrush" : "DangerBrush");

    }

    private void UpdateSystemControlAvailability()
    {
        bool ready = !_systemControlsBusy;
        foreach (var pair in _serviceToggles)
        {
            pair.Value.IsEnabled = ready &&
                                  _lastServiceStates.TryGetValue(pair.Key, out ServiceProbe state) &&
                                  state.Exists;
        }

        bool defenderReady = ready && _lastDefenderStatus?.Available == true;
        DefenderRealtimeCheck.IsEnabled = defenderReady;
        DefenderLowCpuCheck.IsEnabled = defenderReady;
        DefenderIdleScanCheck.IsEnabled = defenderReady;
        DefenderScheduledScanCheck.IsEnabled = defenderReady;
        DefenderCpuLimitCombo.IsEnabled = defenderReady;
        DefenderLowImpactButton.IsEnabled = defenderReady;
        ScheduledDefragCheck.IsEnabled = ready && _lastScheduledTaskStatus.Available;
        RefreshDefenderButton.IsEnabled = ready;
        RefreshServicesButton.IsEnabled = ready;
    }

    private async void OnServiceToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_updatingSystemControls || _systemControlsBusy || sender is not CheckBox check || check.Tag is not string serviceName)
            return;

        if (!ElevationService.IsElevated)
        {
            ShowAdminRequired(LK.MsgServicesNeedsAdmin);
            await RefreshSystemControlsAsync(applyRememberedPauses: false);
            return;
        }

        bool wantedRunning = check.IsChecked == true;
        _systemControlsBusy = true;
        UpdateSystemControlAvailability();
        try
        {
            var result = await Task.Run(() => ServiceControlService.SetRunning(serviceName, wantedRunning));
            if (result.Ok)
            {
                RememberServicePause(serviceName, paused: !wantedRunning);
                _logger.Info(Loc.F(LK.LogServiceToggleFormat, serviceName, result.Message));
            }
            else
            {
                _logger.Warn(Loc.F(LK.MsgServiceToggleFailedFormat, serviceName, result.Message));
                System.Windows.MessageBox.Show(
                    Loc.F(LK.MsgServiceToggleFailedFormat, serviceName, result.Message),
                    Loc.T(LK.AppTitle), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _systemControlsBusy = false;
        }

        await RefreshSystemControlsAsync(applyRememberedPauses: false);
    }

    private async void OnScheduledDefragToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_updatingSystemControls || _systemControlsBusy) return;

        if (!_lastScheduledTaskStatus.Available)
        {
            ShowAdminRequired(LK.MsgScheduledTaskUnavailable);
            await RefreshSystemControlsAsync(applyRememberedPauses: false);
            return;
        }

        if (!ElevationService.IsElevated)
        {
            ShowAdminRequired(LK.MsgServicesNeedsAdmin);
            await RefreshSystemControlsAsync(applyRememberedPauses: false);
            return;
        }

        bool enabled = ScheduledDefragCheck.IsChecked == true;
        _systemControlsBusy = true;
        UpdateSystemControlAvailability();
        try
        {
            var result = await Task.Run(() => ScheduledTaskService.SetEnabled(enabled));
            if (result.Ok)
            {
                _logger.Info(Loc.F(LK.LogServiceToggleFormat, "ScheduledDefrag", result.Message));
            }
            else
            {
                _logger.Warn(Loc.F(LK.MsgServiceToggleFailedFormat, "ScheduledDefrag", result.Message));
                System.Windows.MessageBox.Show(
                    Loc.F(LK.MsgServiceToggleFailedFormat, "ScheduledDefrag", result.Message),
                    Loc.T(LK.AppTitle), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _systemControlsBusy = false;
        }

        await RefreshSystemControlsAsync(applyRememberedPauses: false);
    }

    private void RememberServicePause(string serviceName, bool paused)
    {
        var services = _settings.PausedServicesOnStartup;
        if (paused)
        {
            if (!services.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
                services.Add(serviceName);
        }
        else
        {
            services.RemoveAll(x => x.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
        }

        try
        {
            _settings.Save(AppPaths.SettingsFile);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex.Message);
        }
    }

    private async void OnDefenderRealtimeClicked(object sender, RoutedEventArgs e)
    {
        if (_updatingSystemControls || _systemControlsBusy) return;

        if (!ElevationService.IsElevated)
        {
            ShowAdminRequired(LK.MsgDefenderNeedsAdmin);
            await RefreshSystemControlsAsync(applyRememberedPauses: false);
            return;
        }

        bool enabled = DefenderRealtimeCheck.IsChecked == true;
        if (!enabled)
        {
            var answer = System.Windows.MessageBox.Show(
                Loc.T(LK.MsgDefenderRealtimeConfirm),
                Loc.T(LK.AppTitle),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                DefenderRealtimeCheck.IsChecked = true;
                return;
            }
        }

        await ExecuteDefenderChangeAsync(
            () => DefenderService.SetRealtimeProtection(enabled),
            Loc.F(LK.LogDefenderPreferenceChangedFormat, Loc.T(LK.LabelDefenderRealtime)));
    }

    private async void OnDefenderLowCpuClicked(object sender, RoutedEventArgs e)
    {
        if (_updatingSystemControls || _systemControlsBusy) return;
        bool enabled = DefenderLowCpuCheck.IsChecked == true;
        await ExecuteDefenderChangeAsync(
            () => DefenderService.SetLowCpuPriority(enabled),
            Loc.F(LK.LogDefenderPreferenceChangedFormat, Loc.T(LK.LabelDefenderLowCpu)));
    }

    private async void OnDefenderIdleScanClicked(object sender, RoutedEventArgs e)
    {
        if (_updatingSystemControls || _systemControlsBusy) return;
        bool enabled = DefenderIdleScanCheck.IsChecked == true;
        await ExecuteDefenderChangeAsync(
            () => DefenderService.SetScanOnlyIfIdle(enabled),
            Loc.F(LK.LogDefenderPreferenceChangedFormat, Loc.T(LK.LabelDefenderIdleScan)));
    }

    private async void OnDefenderScheduledScanClicked(object sender, RoutedEventArgs e)
    {
        if (_updatingSystemControls || _systemControlsBusy) return;
        bool enabled = DefenderScheduledScanCheck.IsChecked == true;
        await ExecuteDefenderChangeAsync(
            () => DefenderService.SetScheduledScan(enabled),
            Loc.F(LK.LogDefenderPreferenceChangedFormat, Loc.T(LK.LabelDefenderScheduledScan)));
    }

    private async void OnDefenderCpuLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSystemControls || _systemControlsBusy) return;
        int[] limits = { 20, 30, 50, 70, 100 };
        int index = DefenderCpuLimitCombo.SelectedIndex;
        if (index < 0 || index >= limits.Length) return;

        int limit = limits[index];
        await ExecuteDefenderChangeAsync(
            () => DefenderService.SetCpuLimit(limit),
            Loc.F(LK.LogDefenderPreferenceChangedFormat, Loc.T(LK.LabelDefenderCpuLimit)));
    }

    private async void OnApplyDefenderLowImpactClicked(object sender, RoutedEventArgs e)
    {
        await ExecuteDefenderChangeAsync(
            DefenderService.ApplyLowImpact,
            Loc.T(LK.LogDefenderPreferencesUpdated));
    }

    private async Task ExecuteDefenderChangeAsync(Func<DefenderOperationResult> action, string successLogMessage)
    {
        if (_systemControlsBusy) return;
        if (!ElevationService.IsElevated)
        {
            ShowAdminRequired(LK.MsgDefenderNeedsAdmin);
            await RefreshSystemControlsAsync(applyRememberedPauses: false);
            return;
        }

        _systemControlsBusy = true;
        UpdateSystemControlAvailability();
        try
        {
            var result = await Task.Run(action);
            if (result.Ok)
            {
                _logger.Info(successLogMessage);
            }
            else
            {
                string message = Loc.F(LK.MsgDefenderPreferenceFailedFormat, result.Message);
                _logger.Warn(message);
                System.Windows.MessageBox.Show(message, Loc.T(LK.AppTitle), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _systemControlsBusy = false;
        }

        await RefreshSystemControlsAsync(applyRememberedPauses: false);
    }

    private static void ShowAdminRequired(LK messageKey)
    {
        System.Windows.MessageBox.Show(Loc.T(messageKey), Loc.T(LK.AppTitle),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnAutoStartClicked(object sender, RoutedEventArgs e)
    {
        bool wanted = AutoStartCheck.IsChecked == true;

        if (wanted && !ElevationService.IsElevated)
        {
            AutoStartCheck.IsChecked = false;
            System.Windows.MessageBox.Show(Loc.T(LK.MsgAutoStartNeedsAdmin), Loc.T(LK.AppTitle),
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            System.Windows.MessageBox.Show(result.Message, Loc.T(LK.AppTitle), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            System.Windows.MessageBox.Show(Loc.F(LK.MsgOpenDirFailedFormat, ex.Message), Loc.T(LK.AppTitle),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopyDouyinClicked(object sender, RoutedEventArgs e) => CopyAccount(DouyinText.Text, LK.LabelDouyinId);

    private void OnCopyXiaohongshuClicked(object sender, RoutedEventArgs e) => CopyAccount(XiaohongshuText.Text, LK.LabelXiaohongshuId);

    private void CopyAccount(string text, LK labelKey)
    {
        string label = Loc.T(labelKey);
        try
        {
            Clipboard.SetText(text);
            AboutCopyHint.Text = Loc.F(LK.CopyOkFormat, label, text);
            _logger.Info(Loc.F(LK.CopyOkFormat, label, text));
        }
        catch (Exception ex)
        {
            AboutCopyHint.Text = Loc.F(LK.CopyFailedFormat, ex.Message);
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
            System.Windows.MessageBox.Show(Loc.F(LK.MsgOpenProjectFailedFormat, ex.Message), Loc.T(LK.AppTitle),
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            e.Cancel = true;
            _logger.Warn(Loc.T(LK.LogTrayUnavailableExit));
            ExitApplication();
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            Loc.T(LK.MsgExitOrMinimize),
            Loc.T(LK.AppTitle),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            ExitApplication();
            return;
        }

        // 取消：保持窗口；否：最小化到托盘继续监控。
        e.Cancel = true;
        if (answer == MessageBoxResult.Cancel) return;

        Hide();
        if (_settings.ShowBalloon)
            _tray.ShowBalloon(Loc.T(LK.BalloonHiddenTitle), Loc.T(LK.BalloonHiddenBody));
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
        _developerHeartbeat.Stop();
        _developerLogWindow?.Close();
        try { _engine.ReleaseTarget(Loc.T(LK.ReasonExit)); } catch { }
        try { _engine.Stop(); } catch { }
        try { _tray.Dispose(); } catch { }
        try { _developerRecorder.Dispose(); } catch { }
        try { _logger.Dispose(); } catch { }   // 把队列里的日志写完
        System.Windows.Application.Current.Shutdown();
    }
}
