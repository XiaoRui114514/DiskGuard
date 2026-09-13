using System.Threading;
using System.IO;
using System.Windows;
using DiskGuard.App.Services;
using DiskGuard.Core.Config;
using DiskGuard.Core.Engine;
using DiskGuard.Core.Localization;
using DiskGuard.Core.Logging;
using DiskGuard.Core.Util;

namespace DiskGuard.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private AppLogger? _logger;
    private GuardEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 先读设置并定下界面语言：单实例提示、异常弹窗、托盘日志都要用对语言
        var settings = AppSettings.Load(AppPaths.SettingsFile);
        Loc.SetLanguage(Loc.FromCode(settings.Language));

        _mutex = new Mutex(true, @"Local\DiskGuard_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(Loc.T(LK.MsgSingleInstance), Loc.T(LK.AppTitle),
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(Loc.F(LK.MsgUnhandledFormat, args.Exception.Message), Loc.T(LK.AppTitle),
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var logger = new AppLogger
        {
            WriteToFile = settings.WriteLogFile,
            LogDirectory = AppPaths.LogDirectory
        };
        _logger = logger;

        // 首次运行即生成默认配置文件，方便用户查看/备份
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) settings.Save(AppPaths.SettingsFile);
        }
        catch
        {
            // 配置目录不可写时忽略
        }

        var engine = new GuardEngine(settings, logger);
        var window = new MainWindow(engine, settings, logger);
        _engine = engine;
        MainWindow = window;

        if (settings.StartMinimized) window.Hide();
        else window.Show();

        engine.Start();

        // 注销/关机时也要还原限速（正常退出路径由主窗口负责）
        SessionEnding += (_, _) =>
        {
            try { _engine?.Stop(); } catch { }
        };

        // 开机自启：默认开启；以管理员运行时自动创建计划任务（登录后静默启动，免 UAC 弹窗）
        // 创建/查询计划任务要等 schtasks.exe，放到后台线程，避免启动时界面卡住。
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (settings.AutoStart)
                {
                    if (ElevationService.IsElevated)
                    {
                        if (!AutoStartService.IsEnabled())
                        {
                            var result = AutoStartService.Enable(AutoStartService.ExecutablePath);
                            logger.Info(result.Ok
                                ? Loc.T(LK.LogAutoStartCreated)
                                : Loc.F(LK.LogAutoStartCreateFailedFormat, result.Message));
                        }
                    }
                    else
                    {
                        logger.Warn(Loc.T(LK.LogAutoStartNeedAdmin));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(Loc.F(LK.LogAutoStartHandleFailedFormat, ex.Message));
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _engine?.Stop(); } catch { }
        try { _logger?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
