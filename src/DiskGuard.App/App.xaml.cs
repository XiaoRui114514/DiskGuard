using System.Threading;
using System.IO;
using System.Windows;
using DiskGuard.App.Services;
using DiskGuard.Core.Config;
using DiskGuard.Core.Engine;
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
        _mutex = new Mutex(true, @"Local\DiskGuard_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("磁盘守护已在运行，请在任务栏托盘中查看。", "磁盘守护",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show("发生未处理错误：" + args.Exception.Message, "磁盘守护",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var settings = AppSettings.Load(AppPaths.SettingsFile);
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
                                ? "已按设置创建开机自启任务（登录后自动以管理员权限启动）。"
                                : "创建开机自启任务失败：" + result.Message);
                        }
                    }
                    else
                    {
                        logger.Warn("已开启开机自启，但当前未以管理员运行，无法创建计划任务；点界面右下角「以管理员身份重启」后会自动创建。");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn("处理开机自启失败：" + ex.Message);
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
