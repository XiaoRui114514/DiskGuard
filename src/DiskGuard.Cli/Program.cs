using System.Diagnostics;
using System.Text;
using DiskGuard.Core.Interop;
using DiskGuard.Core.Localization;
using DiskGuard.Core.Logging;
using DiskGuard.Core.Monitoring;
using DiskGuard.Core.Throttling;
using DiskGuard.Core.Util;

namespace DiskGuard.Cli;

/// <summary>命令行自检/测试工具（不参与界面，用于验证采样与限速是否真实生效）。</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var options = ParseOptions(args.Skip(1).ToArray());

        if (options.TryGetValue("out", out var outputFile) && !string.IsNullOrWhiteSpace(outputFile))
        {
            try
            {
                var writer = new StreamWriter(outputFile, false, new UTF8Encoding(true)) { AutoFlush = true };
                Console.SetOut(writer);
            }
            catch
            {
                // 输出重定向失败时继续使用控制台
            }
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "sample": return Sample(options);
                case "etwtest": return EtwTest(options);
                case "load": return Load(options);
                case "throttle": return Throttle(options);
                case "prio": return Prio(options);
                case "nttest": return NtTest(options);
                case "etwdump": return EtwDump(options);
                case "i18n": return I18n();
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("命令执行失败: " + ex);
            Console.Out.Flush();
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "DiskGuard-cli-error.log"), ex.ToString());
            }
            catch
            {
                // 忽略写入失败
            }
            return 3;
        }
    }

    /// <summary>
    /// 校验多语言词条完整性：列出各语言缺失的词条（发布前/CI 用，缺任何一条都算失败）。
    /// 用法：DiskGuard.Cli.exe i18n
    /// </summary>
    private static int I18n()
    {
        int missingTotal = 0;
        foreach (var (language, code, nativeName) in Loc.Options)
        {
            var missing = Loc.MissingKeys(language);
            missingTotal += missing.Count;
            Console.WriteLine(missing.Count == 0
                ? $"OK   {code,-8} {nativeName}"
                : $"缺失 {code,-8} {nativeName}: {string.Join(", ", missing)}");
        }

        int keyCount = Enum.GetValues<LK>().Length;
        Console.WriteLine($"词条总数：{keyCount}");
        Console.WriteLine(missingTotal == 0 ? "多语言词条完整。" : $"共缺失 {missingTotal} 条词条。");
        return missingTotal == 0 ? 0 : 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        DiskGuard 自检工具
          sample   [--seconds N] [--disk X]              打印磁盘忙率与进程磁盘速率排行
          etwtest  [--seconds N] [--disk X]              仅测试 ETW 精确统计
          load     [--mb N] [--seconds N] [--read]       生成磁盘负载（测试用，结束自动删除临时文件）
                   [--random] [--path 文件]             4K 随机写（FUA），模拟浏览器/聊天软件数据库的小 IO
          throttle --pid N [--cap MB] [--iops N] [--seconds N]
                                                        对指定进程限速并在结束后还原
                   [--io 0|1] [--no-cpu] [--suspend 运行ms:挂起ms]
          prio     --pid N                               读取进程当前 IO/CPU 优先级（自检）
          nttest   --pid N                               诊断 IO 优先级查询的调用方式
          etwdump  [--seconds N]                         打印内核磁盘事件的实际字段（诊断用）
          i18n                                           校验 6 种界面语言的词条是否齐全
        """);
    }

    private static int EtwDump(Dictionary<string, string> options)
    {
        int seconds = GetInt(options, "seconds", 6);
        int limit = GetInt(options, "limit", 12);
        int count = 0;

        using var session = new Microsoft.Diagnostics.Tracing.Session.TraceEventSession(
            $"DiskGuardDump_{Environment.ProcessId}") { StopOnDispose = true };
        session.EnableProvider(DiskGuard.Core.Monitoring.EtwProcessIoSource.KernelDiskProviderGuid,
            Microsoft.Diagnostics.Tracing.TraceEventLevel.Verbose, ulong.MaxValue);

        session.Source.Dynamic.All += data =>
        {
            int index = Interlocked.Increment(ref count);
            if (index > limit) return;

            try
            {
                string payload = string.Join(", ", data.PayloadNames.Select(n =>
                {
                    object? value = data.PayloadByName(n);
                    return $"{n}={value}";
                }));
                Console.WriteLine($"[{data.EventName}] pid={data.ProcessID} {payload}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{data.EventName}] 读取字段失败: {ex.Message}");
            }
        };

        var thread = new Thread(() => session.Source.Process()) { IsBackground = true };
        thread.Start();
        Thread.Sleep(seconds * 1000);
        Console.WriteLine($"共收到 {count} 个事件");
        return 0;
    }

    private static int NtTest(Dictionary<string, string> options)
    {
        int pid = GetInt(options, "pid", -1);
        if (pid <= 0)
        {
            Console.WriteLine("需要 --pid 参数");
            return 1;
        }

        Console.WriteLine($"进程架构={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} IntPtr.Size={IntPtr.Size}");
        IntPtr handle = NtOpenProcess(0x0400 | 0x1000, false, pid);
        Console.WriteLine($"openProcess=[{handle}] err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        if (handle == IntPtr.Zero) return 2;

        try
        {
            int v1 = -1;
            int s1 = NtQueryVariant1(handle, 33, ref v1, 4);
            Console.WriteLine($"V1 ref int / len=4 : status=0x{s1:X8} value={v1}");

            int v2 = -1;
            int s2 = NtQueryVariant1(handle, 33, ref v2, 8);
            Console.WriteLine($"V2 ref int / len=8 : status=0x{s2:X8} value={v2}");

            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
            try
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(buffer, -1);
                int s3 = NtQueryVariantPtr(handle, 33, buffer, 4);
                Console.WriteLine($"V3 unmanaged / len=4 : status=0x{s3:X8} value={System.Runtime.InteropServices.Marshal.ReadInt32(buffer)}");

                System.Runtime.InteropServices.Marshal.WriteInt32(buffer, -1);
                int s4 = NtQueryVariantPtr(handle, 33, buffer, 8);
                Console.WriteLine($"V4 unmanaged / len=8 : status=0x{s4:X8} value={System.Runtime.InteropServices.Marshal.ReadInt32(buffer)}");

                IntPtr module = GetModuleHandleW("ntdll.dll");
                IntPtr function = GetProcAddress(module, "NtQueryInformationProcess");
                var direct = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<NtQueryInfoDelegate>(function);

                System.Runtime.InteropServices.Marshal.WriteInt32(buffer, -1);
                int s5 = direct(handle, 33, buffer, 4);
                Console.WriteLine($"V5 GetProcAddress / len=4 : status=0x{s5:X8} value={System.Runtime.InteropServices.Marshal.ReadInt32(buffer)}");

                IntPtr basic = System.Runtime.InteropServices.Marshal.AllocHGlobal(64);
                try
                {
                    int s6 = NtQueryVariantPtr(handle, 0, basic, 48);
                    Console.WriteLine($"V6 class0(ProcessBasicInformation) / len=48 : status=0x{s6:X8}");
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(basic);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }

            long ticks;
            int s8 = NtQuerySystemTime(out ticks);
            Console.WriteLine($"V8 NtQuerySystemTime : status=0x{s8:X8} ticks={ticks}");

            IntPtr self = NtOpenProcess(0x0400 | 0x1000, false, Environment.ProcessId);
            if (self != IntPtr.Zero)
            {
                try
                {
                    int selfValue = -1;
                    int s9 = NtQueryVariant1(self, 33, ref selfValue, 4);
                    Console.WriteLine($"V9 class33 查询自身 : status=0x{s9:X8} value={selfValue}");
                }
                finally
                {
                    NtCloseHandle(self);
                }
            }

            IntPtr big = System.Runtime.InteropServices.Marshal.AllocHGlobal(256);
            try
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(big, -1);
                int s7 = NtQueryVariantPtr(handle, 33, big, 256);
                Console.WriteLine($"V7 unmanaged / len=256 : status=0x{s7:X8} value={System.Runtime.InteropServices.Marshal.ReadInt32(big)}");
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(big);
            }
        }
        finally
        {
            NtCloseHandle(handle);
        }

        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern IntPtr NtOpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern bool NtCloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryVariant1(IntPtr handle, int infoClass, ref int value, int length);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryVariantPtr(IntPtr handle, int infoClass, IntPtr buffer, int length);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "NtQuerySystemTime")]
    private static extern int NtQuerySystemTime(out long systemTime);

    private static int Prio(Dictionary<string, string> options)
    {
        int pid = GetInt(options, "pid", -1);
        if (pid <= 0)
        {
            Console.WriteLine("需要 --pid 参数");
            return 1;
        }

        var (io, priorityClass, ioStatus) = ProcessPriorityReader.ReadDetailed(pid);
        Console.WriteLine($"PID {pid}: IO 优先级 = {io} ({ProcessPriorityReader.IoPriorityText(io)}), CPU 优先级 = 0x{priorityClass:X}, IO 查询状态 = 0x{ioStatus:X8}");
        return 0;
    }

    private static int Sample(Dictionary<string, string> options)
    {
        int seconds = GetInt(options, "seconds", 6);
        int disk = GetInt(options, "disk", 0);

        using var sampler = new DiskSampler();
        var logger = new AppLogger { WriteToFile = false };
        using var io = ProcessIoSourceFactory.Create(disk, m => Console.WriteLine("[来源] " + m));

        Console.WriteLine($"管理员权限: {PrivilegeHelper.IsElevated}");
        Console.WriteLine("说明: 占比 = 该进程占磁盘忙碌时间的百分比（" + io.ShareMetricName + "）");
        Console.WriteLine();

        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            sampler.Collect();
            var status = sampler.GetDisk(disk);
            var samples = io.Snapshot(1.0);

            double totalIops = samples.Sum(s => s.IoCount);
            double windowMs = 1000;
            double busyNow = status?.BusyPercent ?? 0;

            double Occupancy(ProcessIoSample s) => io.ProvidesServiceTime
                ? Math.Clamp(s.ServiceTimeMs / windowMs * 100, 0, 100)
                : totalIops > 0 ? Math.Clamp(s.IoCount / totalIops * Math.Clamp(busyNow, 0, 100), 0, 100) : 0;

            samples = samples.OrderByDescending(Occupancy).ToList();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {status?.DisplayName ?? "磁盘" + disk} 忙率 {status?.BusyPercent ?? 0:0.0}%  队列 {status?.QueueLength ?? 0:0.00}  读 {ProcessUtil.FormatRate(status?.ReadBytesPerSec ?? 0)}  写 {ProcessUtil.FormatRate(status?.WriteBytesPerSec ?? 0)}");
            int shown = 0;
            foreach (var sample in samples)
            {
                if (sample.Pid <= 0) continue;
                double occupancy = Occupancy(sample);

                if (occupancy < 0.5 && sample.TotalBytesPerSec < 1024 && !options.ContainsKey("debug")) continue;
                Console.WriteLine($"     {ResolveName(sample.Pid, sample.Name),-30} PID {sample.Pid,-7} 占用 {occupancy,5:0.0}%  合计 {ProcessUtil.FormatRate(sample.TotalBytesPerSec),-12} IO次数 {sample.IoCount,8:0}  服务时间 {sample.ServiceTimeMs,9:0} ms");
                if (++shown >= (options.ContainsKey("debug") ? 12 : 8)) break;
            }
        }

        return 0;
    }

    private static int EtwTest(Dictionary<string, string> options)
    {
        int seconds = GetInt(options, "seconds", 6);
        int disk = GetInt(options, "disk", 0);

        Console.WriteLine($"管理员权限: {PrivilegeHelper.IsElevated}");
        bool useKernelMode = options.ContainsKey("kernel");
        using var io = new EtwProcessIoSource(disk, usePrivateSession: !useKernelMode);
        Console.WriteLine($"会话模式: {(useKernelMode ? "系统日志器" : "私有会话 + 内核清单提供程序")}");
        Thread.Sleep(800);
        Console.WriteLine("ETW 状态: " + (string.IsNullOrEmpty(io.Error) ? "运行中" : "失败 - " + io.Error));

        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            var samples = io.Snapshot(1.0).OrderByDescending(s => s.TotalBytesPerSec).ToList();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 会话继续运行: {(string.IsNullOrEmpty(io.Error) ? "是" : "否 - " + io.Error)}  已接收事件: {io.EventCount}");
            foreach (var sample in samples.Take(6))
                Console.WriteLine($"     {ResolveName(sample.Pid, sample.Name),-30} PID {sample.Pid,-7} 读 {ProcessUtil.FormatRate(sample.ReadBytesPerSec),-12} 写 {ProcessUtil.FormatRate(sample.WriteBytesPerSec)}");
        }

        return string.IsNullOrEmpty(io.Error) ? 0 : 2;
    }

    private static int Load(Dictionary<string, string> options)
    {
        int megabytes = GetInt(options, "mb", 1024);
        int seconds = GetInt(options, "seconds", 15);
        bool readMode = options.ContainsKey("read");
        bool bufferedMode = options.ContainsKey("buffered");
        bool randomMode = options.ContainsKey("random");
        string path = Get(options, "path", Path.Combine(Path.GetTempPath(), "DiskGuard-load.tmp"));
        bool keep = options.ContainsKey("keep");

        long size = (long)megabytes * 1024 * 1024;
        var buffer = new byte[8 * 1024 * 1024];
        Random random = new(12345);
        random.NextBytes(buffer);

        try
        {
            var prepareWatch = Stopwatch.StartNew();
            if (!File.Exists(path) || new FileInfo(path).Length < size)
            {
                using var create = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 20, FileOptions.None);
                create.SetLength(size);
            }
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 文件准备完成（{prepareWatch.Elapsed.TotalSeconds:0.0}s）");

            Console.WriteLine($"PID={Environment.ProcessId}  文件={path}  大小={megabytes} MB  模式={(randomMode ? "4K 随机写(FUA)" : readMode ? "读" : "写")}  时长={seconds}s");
            Console.Out.Flush();

            if (randomMode)
                return RunRandomLoad(path, size, seconds, keep, buffer, random);

            long total = 0;
            long lastTotal = 0;
            double lastTime = 0;
            long nextFlushAt = 64L * 1024 * 1024;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalSeconds < seconds)
            {
                using var stream = new FileStream(path, FileMode.Open, readMode ? FileAccess.Read : FileAccess.Write,
                    FileShare.ReadWrite, 1 << 20,
                    bufferedMode ? FileOptions.SequentialScan : FileOptions.WriteThrough | FileOptions.RandomAccess);

                long offset = 0;
                while (offset < size && stopwatch.Elapsed.TotalSeconds < seconds)
                {
                    int count = (int)Math.Min(buffer.Length, size - offset);
                    if (readMode) stream.ReadExactly(buffer, 0, count);
                    else stream.Write(buffer, 0, count);
                    offset += count;
                    total += count;

                    if (bufferedMode && !readMode && total >= nextFlushAt)
                    {
                        stream.Flush(true);
                        nextFlushAt += 64L * 1024 * 1024;
                    }

                    double elapsed = stopwatch.Elapsed.TotalSeconds;
                    if (elapsed - lastTime >= 1.0)
                    {
                        double instantaneous = (total - lastTotal) / 1024.0 / 1024.0 / (elapsed - lastTime);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 累计 {total / 1024.0 / 1024.0:0} MB，瞬时 {instantaneous:0} MB/s");
                        Console.Out.Flush();
                        lastTotal = total;
                        lastTime = elapsed;
                    }
                }
            }

            double mbps = total / 1024.0 / 1024.0 / stopwatch.Elapsed.TotalSeconds;
            Console.WriteLine($"完成: 平均 {mbps:0.0} MB/s，总计 {total / 1024.0 / 1024.0:0} MB");
        }
        catch (Exception ex)
        {
            Console.WriteLine("负载生成失败: " + ex.Message);
            return 2;
        }
        finally
        {
            if (!keep)
            {
                try { File.Delete(path); } catch { }
            }
        }

        return 0;
    }

    /// <summary>4KB 随机 FUA 写入：磁盘很忙但吞吐量很低，用于验证"按占用百分比限速"。</summary>
    private static int RunRandomLoad(string path, long size, int seconds, bool keep, byte[] buffer, Random random)
    {
        const int blockSize = 4096;
        long total = 0;
        long lastTotal = 0;
        double lastTime = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                blockSize, FileOptions.WriteThrough | FileOptions.RandomAccess);

            while (stopwatch.Elapsed.TotalSeconds < seconds)
            {
                long offset = random.NextInt64(0, Math.Max(blockSize, size - blockSize));
                offset &= ~(long)(blockSize - 1);
                stream.Position = offset;
                stream.Write(buffer, 0, blockSize);
                total += blockSize;

                double elapsed = stopwatch.Elapsed.TotalSeconds;
                if (elapsed - lastTime >= 1.0)
                {
                    double delta = elapsed - lastTime;
                    double instantaneous = (total - lastTotal) / 1024.0 / 1024.0 / delta;
                    double iops = (total - lastTotal) / (double)blockSize / delta;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 累计 {total / 1024.0 / 1024.0:0.0} MB，瞬时 {instantaneous:0.00} MB/s，IOPS {iops:0}");
                    Console.Out.Flush();
                    lastTotal = total;
                    lastTime = elapsed;
                }
            }

            Console.WriteLine($"完成: 平均 {total / 1024.0 / 1024.0 / stopwatch.Elapsed.TotalSeconds:0.00} MB/s，总计 {total / 1024.0 / 1024.0:0.0} MB");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("随机负载生成失败: " + ex.Message);
            return 2;
        }
        finally
        {
            if (!keep)
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    private static int Throttle(Dictionary<string, string> options)
    {
        int pid = GetInt(options, "pid", -1);
        if (pid <= 0)
        {
            Console.WriteLine("需要 --pid 参数");
            return 1;
        }

        double capMB = GetDouble(options, "cap", -1);
        int iops = GetInt(options, "iops", (int)DiskGuard.Core.Engine.GuardEngine.DefaultCapIops);
        int seconds = GetInt(options, "seconds", 15);
        int ioPriority = GetInt(options, "io", 0);
        bool lowerCpu = !options.ContainsKey("no-cpu");
        string suspend = Get(options, "suspend", string.Empty);
        bool stopExisting = options.ContainsKey("release");

        var throttler = new ProcessThrottler();
        if (stopExisting)
        {
            Console.WriteLine("仅支持限速/还原同一进程；使用 --seconds 控制限速时长。");
            return 1;
        }

        string name = ResolveName(pid, string.Empty);
        var handle = throttler.ApplyLevel1(pid, name, true, ioPriority, lowerCpu);
        if (handle == null)
        {
            Console.WriteLine($"一级限速失败: {throttler.LastErrorText} (err={throttler.LastError})");
            return 2;
        }

        Console.WriteLine($"一级限速已应用: {name} (PID {pid})");
        Console.WriteLine($"  IO 优先级: {PriorityText(handle.OriginalIoPriority)} -> {PriorityText(handle.AppliedIoPriority)}");
        Console.WriteLine($"  CPU 优先级: 0x{handle.OriginalPriorityClass:X} -> 0x{handle.AppliedPriorityClass:X}");

        bool level2 = false;
        if (capMB > 0)
        {
            long cap = (long)(capMB * 1024 * 1024);
            level2 = throttler.ApplyLevel2(handle, cap, iops);
            Console.WriteLine(level2
                ? $"  二级限速已应用: 吞吐上限 {capMB:0.#} MB/s + {iops} IOPS"
                : $"  二级限速失败: {throttler.LastErrorText} (err={throttler.LastError})");
        }

        if (!string.IsNullOrEmpty(suspend))
        {
            var parts = suspend.Split(':');
            int runMs = parts.Length > 0 && int.TryParse(parts[0], out int r) ? r : 300;
            int pauseMs = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 200;
            bool ok = throttler.StartLevel3(handle, runMs, pauseMs);
            Console.WriteLine(ok ? $"  三级限速已应用: {runMs}ms 运行 / {pauseMs}ms 挂起" : "  三级限速失败");
        }

        Console.WriteLine();
        Console.WriteLine("开始观察目标进程吞吐（每秒一行）：");

        using var sampler = new DiskSampler();
        using var ioSource = new IoCountersProcessIoSource();
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            sampler.Collect();
            var disk = sampler.GetDisk(0);
            var samples = ioSource.Snapshot(1.0);
            var target = samples.FirstOrDefault(s => s.Pid == pid);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 磁盘0 忙率 {disk?.BusyPercent ?? 0,5:0.0}%  目标进程 {ProcessUtil.FormatRate(target?.TotalBytesPerSec ?? 0),-12} {(target == null ? "(无活动)" : "")}");
        }

        throttler.Release(handle);
        Console.WriteLine();
        Console.WriteLine("已还原限速，验证还原结果：");

        var (ioNow, cpuNow) = ProcessPriorityReader.Read(pid);
        Console.WriteLine($"  IO 优先级: {PriorityText(ioNow)} (原始 {PriorityText(handle.OriginalIoPriority)}；本环境可能无法查询，已按默认值还原)");
        Console.WriteLine($"  CPU 优先级: 0x{cpuNow:X} (原始 0x{handle.OriginalPriorityClass:X})");

        return level2 || capMB <= 0 ? 0 : 3;
    }

    private static string PriorityText(int value) => value switch
    {
        0 => "极低(0)",
        1 => "低(1)",
        2 => "普通(2)",
        3 => "高(3)",
        _ => "未知(-1)"
    };

    private static string ResolveName(int pid, string fallback)
    {
        if (!string.IsNullOrEmpty(fallback)) return fallback;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "PID " + pid;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
            string key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[i + 1];
                i++;
            }
            else
            {
                result[key] = "1";
            }
        }
        return result;
    }

    private static int GetInt(Dictionary<string, string> options, string key, int fallback)
        => options.TryGetValue(key, out var value) && int.TryParse(value, out int parsed) ? parsed : fallback;

    private static double GetDouble(Dictionary<string, string> options, string key, double fallback)
        => options.TryGetValue(key, out var value) && double.TryParse(value, out double parsed) ? parsed : fallback;

    private static string Get(Dictionary<string, string> options, string key, string fallback)
        => options.TryGetValue(key, out var value) ? value : fallback;

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
    private delegate int NtQueryInfoDelegate(IntPtr handle, int infoClass, IntPtr buffer, int length);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
