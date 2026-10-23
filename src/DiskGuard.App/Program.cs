using System;
using System.IO;
using DiskGuard.Core.Util;

namespace DiskGuard.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // WPF 可能在 App.OnStartup 之前就读取 TEMP；必须在创建 Application 前切换，
        // 避免复用系统 TEMP\WPF 里损坏或无权限的缓存。
        try
        {
            Directory.CreateDirectory(AppPaths.TempDirectory);
            Environment.SetEnvironmentVariable("TEMP", AppPaths.TempDirectory);
            Environment.SetEnvironmentVariable("TMP", AppPaths.TempDirectory);
        }
        catch
        {
            // 私有目录不可用时继续使用系统 TEMP。
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
