using DiskGuard.Core.Interop;

namespace DiskGuard.Core.Monitoring;

public static class ProcessIoSourceFactory
{
    /// <summary>清理上次被强杀时残留的 DiskGuard ETW 会话（否则内核提供程序被占用，新会话收不到事件）。</summary>
    public static void CleanupStaleSessions(Action<string> log)
    {
        try
        {
            foreach (string name in Microsoft.Diagnostics.Tracing.Session.TraceEventSession.GetActiveSessionNames())
            {
                if (!name.StartsWith("DiskGuardKernel_", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("DiskGuardDump_", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var stale = Microsoft.Diagnostics.Tracing.Session.TraceEventSession.GetActiveSession(name);
                stale.Stop(true);
                log($"已清理上次异常退出残留的 ETW 会话：{name}");
            }
        }
        catch (Exception ex)
        {
            log("清理残留 ETW 会话失败：" + ex.Message);
        }
    }

    /// <summary>优先使用 ETW 精确统计（需管理员），失败时回退 PDH 近似统计。</summary>
    public static IProcessIoSource Create(int diskNumber, Action<string> log)
    {
        if (PrivilegeHelper.IsElevated)
        {
            // 首选：私有会话 + 内核清单提供程序（不占用 NT Kernel Logger 槽位，避免"系统资源不足"）
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var source = new EtwProcessIoSource(diskNumber, usePrivateSession: true);
                    Thread.Sleep(300);

                    if (string.IsNullOrEmpty(source.Error))
                    {
                        log($"已启用精确统计：ETW 内核磁盘事件（磁盘 {diskNumber}，私有会话）");
                        return source;
                    }

                    string error = source.Error;
                    source.Dispose();
                    log($"ETW 私有会话启动失败（第 {attempt} 次）：{error}");
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    log($"ETW 私有会话启动异常（第 {attempt} 次）：{ex.Message}");
                    Thread.Sleep(500);
                }
            }

            // 回退：传统系统日志器方式
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var source = new EtwProcessIoSource(diskNumber, usePrivateSession: false);
                    Thread.Sleep(300);

                    if (string.IsNullOrEmpty(source.Error))
                    {
                        log($"已启用精确统计：ETW 内核磁盘事件（磁盘 {diskNumber}，系统日志器）");
                        return source;
                    }

                    string error = source.Error;
                    source.Dispose();
                    log($"ETW 系统日志器启动失败（第 {attempt} 次）：{error}");
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    log($"ETW 系统日志器启动异常（第 {attempt} 次）：{ex.Message}");
                    Thread.Sleep(500);
                }
            }

            log("ETW 统计不可用，降级为按字节占比的近似统计（以管理员身份重启通常可恢复精确统计）。");
        }
        else
        {
            log("当前未以管理员运行，使用近似统计（含网络 IO）；以管理员运行可获得精确的磁盘0读写统计。");
        }

        return new IoCountersProcessIoSource();
    }
}
