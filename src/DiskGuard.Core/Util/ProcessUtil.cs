using System.Diagnostics;
using System.Collections.Concurrent;
using DiskGuard.Core.Interop;

namespace DiskGuard.Core.Util;

public static class ProcessUtil
{
    private const double Kilo = 1024;
    private const double Mega = Kilo * 1024;
    private const double Giga = Mega * 1024;

    /// <summary>系统关键进程：任何情况下都不动它们。</summary>
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "idle", "memory compression", "secure system",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "dwm", "audiodg", "fontdrvhost", "wudfhost", "memory",
        // Windows 外壳 / 输入 / 会话相关：限速它们会直接表现为"桌面无响应、打不开程序、打不了字"，
        // 属于用户抱怨的"整个系统卡死"里最不该碰的一批进程，任何情况下都不限速它们。
        "explorer", "sihost", "userinit", "logonui", "taskhostw",
        "startmenuexperiencehost", "shellexperiencehost", "searchhost",
        "textinputhost", "ctfmon", "applicationframehost", "consent"
    };

    public static bool IsSystemCritical(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return CriticalNames.Contains(name);
    }

    private static readonly object NameCacheGate = new();
    private static readonly TimeSpan NameCacheTtl = TimeSpan.FromSeconds(2);
    private static ConcurrentDictionary<int, string> _nameCache = new();
    private static DateTime _nameCacheRefreshedAtUtc = DateTime.MinValue;

    /// <summary>
    /// PID → 进程名映射（2 秒缓存）。
    /// ETW 数据源只给 PID，采样线程每秒都要解析一次名字，缓存可避免每秒枚举全部进程。
    /// </summary>
    public static IReadOnlyDictionary<int, string> GetProcessNames()
    {
        lock (NameCacheGate)
        {
            if (DateTime.UtcNow - _nameCacheRefreshedAtUtc < NameCacheTtl) return _nameCache;
        }

        var map = new ConcurrentDictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                map[process.Id] = process.ProcessName;
            }
            catch
            {
                // 访问受限的进程跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        lock (NameCacheGate)
        {
            _nameCache = map;
            _nameCacheRefreshedAtUtc = DateTime.UtcNow;
        }

        return map;
    }

    /// <summary>解析单个进程名：命中缓存直接返回，否则单独查询并写入缓存（用于刚启动、还没进缓存的进程）。</summary>
    public static string? ResolveProcessName(int pid)
    {
        if (pid <= 0) return null;

        var cache = _nameCache;
        if (cache.TryGetValue(pid, out string? cached)) return cached;

        string? name = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch
        {
            // 进程已退出或无法访问
        }

        if (!string.IsNullOrEmpty(name)) cache[pid] = name;
        return name;
    }

    public static int GetForegroundProcessId()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>父进程链上遇到这些进程就停止向上找，避免把 shell/系统整体算成"前台程序"。</summary>
    private static readonly HashSet<string> FamilyStopNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "sihost", "userinit", "svchost", "services", "dllhost",
        "runtimebroker", "taskhostw", "applicationframehost", "startmenuexperiencehost"
    };

    /// <summary>
    /// 取"同一个程序"的进程集合：自身 + 父进程链（到 shell/系统进程为止）+ 全部子孙进程。
    /// 多进程应用（Chromium/Electron/商店应用）里，前台窗口和真正读写磁盘的往往不是同一个 PID，
    /// 只按单个 PID 保护会把它自己的子进程限速，用户照样卡。
    /// </summary>
    public static HashSet<int> GetProcessFamily(int processId)
    {
        var result = new HashSet<int>();
        if (processId <= 0) return result;

        var parents = new Dictionary<int, int>();
        var names = new Dictionary<int, string>();

        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>()
            };

            if (NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    int id = (int)entry.th32ProcessID;
                    parents[id] = (int)entry.th32ParentProcessID;
                    names[id] = entry.szExeFile ?? string.Empty;
                } while (NativeMethods.Process32NextW(snapshot, ref entry));
            }
        }
        catch
        {
            return result;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        // 先建一次"父 PID → 子 PID 列表"的索引：否则下面按层向下遍历时，
        // 每个节点都要把整张进程表扫一遍（进程多的时候每秒多出上万次比较）。
        var children = new Dictionary<int, List<int>>(parents.Count);
        foreach (var pair in parents)
        {
            if (!children.TryGetValue(pair.Value, out var list))
            {
                list = new List<int>(4);
                children[pair.Value] = list;
            }

            list.Add(pair.Key);
        }

        result.Add(processId);

        // 前台程序本身就是 shell / 系统进程时（比如点了桌面、任务栏），只保护它自己：
        // 否则"桌面"下面的所有子进程（从桌面启动的各种软件）都会被当成前台程序，保护范围失控。
        if (!names.TryGetValue(processId, out string? selfName)) return result;
        string selfSimple = TrimExe(selfName);
        if (IsSystemCritical(selfSimple) || FamilyStopNames.Contains(selfSimple)) return result;

        // 向上：把启动它的父进程也算进来（例如启动器 → 主进程 → 子进程）
        int current = processId;
        for (int depth = 0; depth < 5; depth++)
        {
            if (!parents.TryGetValue(current, out int parent) || parent <= 0 || parent == 4) break;
            if (!names.TryGetValue(parent, out string? parentName)) break;
            string parentSimple = TrimExe(parentName);
            if (IsSystemCritical(parentSimple) || FamilyStopNames.Contains(parentSimple)) break;
            result.Add(parent);
            current = parent;
        }

        // 向下：渲染进程 / GPU 进程 / 工具进程等
        var queue = new Queue<int>(result);
        while (queue.Count > 0 && result.Count < 48)
        {
            int parentId = queue.Dequeue();
            if (!children.TryGetValue(parentId, out var childList)) continue;

            foreach (int child in childList)
            {
                if (!result.Add(child)) continue;
                queue.Enqueue(child);
                if (result.Count >= 48) break;
            }
        }

        return result;
    }

    private static string TrimExe(string name)
    {
        string trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= Giga) return $"{bytesPerSecond / Giga:0.00} GB/s";
        if (bytesPerSecond >= Mega) return $"{bytesPerSecond / Mega:0.0} MB/s";
        if (bytesPerSecond >= Kilo) return $"{bytesPerSecond / Kilo:0} KB/s";
        return $"{bytesPerSecond:0} B/s";
    }

    public static string FormatSize(double bytes)
    {
        if (bytes >= Giga) return $"{bytes / Giga:0.00} GB";
        if (bytes >= Mega) return $"{bytes / Mega:0.0} MB";
        if (bytes >= Kilo) return $"{bytes / Kilo:0} KB";
        return $"{bytes:0} B";
    }
}
