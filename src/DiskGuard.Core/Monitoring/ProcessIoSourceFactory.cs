using DiskGuard.Core.Interop;
using DiskGuard.Core.Localization;

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
                log(Loc.F(LK.IoSourceCleanupStaleFormat, name));
            }
        }
        catch (Exception ex)
        {
            log(Loc.F(LK.IoSourceCleanupFailedFormat, ex.Message));
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
                        log(Loc.F(LK.IoSourceEtwPrivateOkFormat, diskNumber));
                        return source;
                    }

                    string error = source.Error;
                    source.Dispose();
                    log(Loc.F(LK.IoSourceEtwPrivateFailedFormat, attempt, error));
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    log(Loc.F(LK.IoSourceEtwPrivateErrorFormat, attempt, ex.Message));
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
                        log(Loc.F(LK.IoSourceEtwSystemOkFormat, diskNumber));
                        return source;
                    }

                    string error = source.Error;
                    source.Dispose();
                    log(Loc.F(LK.IoSourceEtwSystemFailedFormat, attempt, error));
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    log(Loc.F(LK.IoSourceEtwSystemErrorFormat, attempt, ex.Message));
                    Thread.Sleep(500);
                }
            }

            log(Loc.T(LK.IoSourceEtwUnavailable));
        }
        else
        {
            log(Loc.T(LK.IoSourceNotElevated));
        }

        return new IoCountersProcessIoSource();
    }
}
