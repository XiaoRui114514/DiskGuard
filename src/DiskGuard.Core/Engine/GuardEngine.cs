using System.Diagnostics;
using DiskGuard.Core.Config;
using DiskGuard.Core.Interop;
using DiskGuard.Core.Localization;
using DiskGuard.Core.Logging;
using DiskGuard.Core.Monitoring;
using DiskGuard.Core.Throttling;
using DiskGuard.Core.Util;

namespace DiskGuard.Core.Engine;

/// <summary>
/// 监控磁盘忙率，并在持续高负载时对“占用最高的可限速进程”执行分级限速，负载回落后自动还原。
/// </summary>
public sealed class GuardEngine : IDisposable
{
    private const double Mega = 1024.0 * 1024.0;

    /// <summary>
    /// 二级限速的 IOPS 初始上限。只限吞吐（MB/s）挡不住小 IO：
    /// 4K 随机读写时进程可能只有几百 KB/s，却因为每秒上万次 IO 把磁盘占满、拖死整个系统，
    /// 所以限速必须同时压 IO 次数（Windows 会按先到的那个上限执行）。
    /// </summary>
    public const long DefaultCapIops = 800;

    /// <summary>IOPS 上限收紧的下限，再低目标进程连正常的小 IO 都做不完。</summary>
    public const long MinCapIops = 8;

    /// <summary>
    /// 初始 IOPS 上限：不是固定 800，而是贴着目标当前的 IO 次数往下压到约 2/3。
    /// 延迟型负载（USB 外置盘、快满的 SSD、大量随机小 IO）占用率很高但 IOPS 并不高：
    /// 例如 1.4 MB/s、每秒 30 次 IO 就能把外置盘占满，此时给它 800 IOPS 或 30 MB/s 的上限毫无作用，
    /// 必须按它自己的 IO 次数收紧，才能真正把磁盘时间让出来。
    /// </summary>
    private static long InitialIopsCapFor(ProcessIoRow row)
    {
        double observed = row.AverageIoCount > 0 ? row.AverageIoCount : row.IoCount;
        if (observed <= 0) return DefaultCapIops;

        long cap = (long)Math.Ceiling(observed * 2 / 3);
        return Math.Clamp(cap, MinCapIops, DefaultCapIops);
    }

    /// <summary>
    /// 够格当限速目标的最低 IO 量：低于这条线说明该进程只是"在等磁盘"，而不是"把磁盘压住"的那个。
    /// 限速它既不能让磁盘变快，还会让它更卡。实测：钉钉自身只有 39 KB/s 的 IO，
    /// 却因为"占磁盘时间 98%"被压到 2 MB/s + 8 IOPS，磁盘忙率纹丝不动（91%~100%）。
    /// </summary>
    private const double MinLoadBytesPerSec = 1 * Mega;

    /// <summary>
    /// "被限速的受害者"判据：IO 次数低于这条线、自己又没有吞吐的进程，只是在等磁盘，不是在压磁盘。
    /// 实测（2026-09-17 日志）：被限速的钉钉、ChatGPT、msedgewebview2 都只有 8~20 次/秒的 IO，
    /// 而真正的源头（svchost / MoUsoCoreWorker / nvcontainer）都是 200 次/秒以上。
    /// 压受害者既不能让磁盘变快，又会让用户面前那个程序直接卡住，所以候选进程必须"自己发得出 IO"。
    /// </summary>
    private const double MinLoadIops = 100;

    /// <summary>
    /// 忙率虚高的判定要用到响应时间：忙率 ≥90% 却没有吞吐、队列为空、响应时间也正常（&lt; 20 ms），
    /// 说明磁盘其实没在干活（计数器异常 / SSD 内部动作），限速任何进程都改善不了。
    /// 反过来，如果响应时间很长，说明磁盘是真的在拖后腿，就该照常挑目标限速。
    /// </summary>
    private const double BogusBusyLatencyMs = 20;

    /// <summary>
    /// "够用即可"的收敛线：磁盘忙率一旦低于 95%，就不再继续收紧限速。
    /// 限速的目的是把磁盘从"几乎 100% 忙、点什么都卡"拉回可用状态，不必把程序一路压到最低档。
    /// </summary>
    private const double BusyReliefPercent = 95;

    /// <summary>
    /// 继续收紧的最低改善量（忙率百分点）。上次收紧之后忙率没有明显下降，说明瓶颈不在这个进程
    /// （或磁盘自身在忙），再往下压只会让程序更卡。实测：钉钉被从 30 MB/s 一路压到 2 MB/s 期间，
    /// 磁盘忙率始终 91%~100% 毫无变化，属于典型的"压了也没用"。
    /// </summary>
    private const double BusyImprovementPercent = 1;

    /// <summary>单块监控磁盘的忙率硬上限；达到此值时前台保护让位于恢复磁盘可用性。</summary>
    private const double DiskBusyHardLimitPercent = 98;

    /// <summary>硬上限触发后，所选磁盘连续两个采样不高于此值才解除自动限速。</summary>
    private const double DiskBusyRecoveryPercent = 95;
    private const int DiskBusyRecoverySamples = 2;

    private static readonly HashSet<int> NoProtectedPids = new();

    /// <summary>保护限速目标状态（_target 及其历史/计数）。界面线程与采样线程都会读写，必须统一加锁。</summary>
    private readonly object _stateSync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, DateTime> _blockedPids = new();
    private readonly List<ActiveThrottleRecord> _records = new();
    private readonly HashSet<int> _activePidScratch = new();
    private readonly List<int> _stalePidScratch = new();

    private AppSettings _settings;
    private readonly AppLogger _log;
    private readonly ProcessThrottler _throttler = new();

    private Thread? _worker;
    private DiskSampler? _diskSampler;
    private IProcessIoSource? _ioSource;
    private volatile bool _restartRequested;
    private volatile bool _paused;
    private int _samplingDiskNumber;
    private readonly object _recordSync = new();
    private List<ActiveThrottleRecord>? _pendingRecords;
    private bool _recordWriterRunning;

    private DateTime? _highSince;
    private DateTime? _lowSince;
    private ThrottleHandle? _target;
    private bool _hardLimitTarget;
    private DateTime _nextInitRetry = DateTime.MinValue;
    private int _targetLevel;
    private DateTime _levelAppliedAt = DateTime.MinValue;
    private int _idleTargetTicks;
    private int _pendingPid = -1;
    private int _pendingCount;
    private bool _warnedUnable;
    private readonly Queue<double> _busyHistory = new();
    private readonly Queue<double> _targetOccupancyHistory = new();
    private readonly Queue<double> _targetBytesHistory = new();
    private readonly Queue<double> _targetIopsHistory = new();
    private readonly Dictionary<int, PidHistory> _rateHistory = new();
    private long _currentCapBytesPerSec;
    private long _currentCapIops;
    private bool _capHoldLogged;
    private bool _capFloorLogged;
    private bool _capIneffectiveLogged;
    /// <summary>上一次收紧上限时的忙率（滑动平均）；用于判断"继续收紧还有没有用"，-1 表示还没收紧过。</summary>
    private double _busyBeforeLastTighten = -1;
    private double _lastTargetOccupancyPercent;
    private string _diskInfoSuffix = string.Empty;
    private DateTime _lastSourceRestart = DateTime.MinValue;
    private long _lastEventCount;
    private DateTime _lastEventGrowth = DateTime.Now;
    private DateTime _lastBusyAt = DateTime.MinValue;
    private int _sourceRebuildCount;
    private int _emergencyTicks;
    private DateTime _lastBlockedCleanup = DateTime.MinValue;
    private int _noThroughputTicks;
    private DateTime _lastNoThroughputLog = DateTime.MinValue;
    private DateTime _lastDefragHint = DateTime.MinValue;
    // 用系统 TickCount 估算 Windows 启动时间，而不是把工具晚启动的时间当作开机时间。
    private readonly DateTime _bootAt = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private long _lastSystemIdleTicks;
    private long _lastSystemKernelTicks;
    private long _lastSystemUserTicks;
    private DateTime _lastSystemCpuAt;

    private sealed class PidHistory
    {
        public readonly Queue<double> Bytes = new();
        public readonly Queue<double> Share = new();
        public readonly Queue<double> Iops = new();
    }

    public GuardEngine(AppSettings settings, AppLogger log)
    {
        _settings = settings;
        _log = log;
        // 需要排查时设置环境变量 DISKGUARD_DIAG=1 可输出每 5 秒一次的采样诊断日志
        _debugDiagnostics = Environment.GetEnvironmentVariable("DISKGUARD_DIAG") == "1";
    }

    public event Action<EngineSnapshot>? SnapshotProduced;

    public bool Running => _worker is { IsAlive: true };
    public bool Paused => _paused;
    public string IoMode => _ioSource?.Mode ?? Loc.T(LK.IoModeStarting);
    public bool IoPrecise => _ioSource?.IsPrecise ?? false;
    public EngineSnapshot? LastSnapshot { get; private set; }

    public void UpdateSettings(AppSettings settings)
    {
        // 注意：界面与引擎共用同一个 AppSettings 实例，不能用 _settings 与 settings 自比，
        // 否则"切换监控磁盘"永远判定为没变化，ETW 数据源不会重建。
        bool diskChanged = _samplingDiskNumber != settings.DiskNumber;
        lock (_stateSync) _settings = settings;
        // 切换监控磁盘要重建 ETW 数据源（内部有等待），交给后台线程做，避免卡住界面
        if (diskChanged && Running) _restartRequested = true;
    }

    public void Start()
    {
        if (Running) return;
        // 全部初始化（PDH、ETW 会话、残留限速还原）都放到工作线程里做，
        // 否则界面线程会被 ETW/磁盘操作阻塞数秒，表现为"点了没反应、打不了字"。
        // 监控线程用低于正常的优先级：它不是实时任务，不和前台程序抢 CPU。
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "DiskGuard-Engine",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _worker?.Join(TimeSpan.FromSeconds(3)); } catch { }
        _worker = null;
        ReleaseTarget(Loc.T(LK.EngineReasonStopped));
        _diskSampler?.Dispose();
        _diskSampler = null;
        _ioSource?.Dispose();
        _ioSource = null;
    }

    /// <summary>在工作线程上初始化/重建采样数据源。</summary>
    private void InitializeSources(bool recreate)
    {
        try
        {
            if (!recreate) RestoreStaleThrottles();
            _diskSampler ??= new DiskSampler();
            if (!recreate) ProcessIoSourceFactory.CleanupStaleSessions(m => _log.Info(m));

            _ioSource?.Dispose();
            _ioSource = ProcessIoSourceFactory.Create(_settings.DiskNumber, m => _log.Info(m));
            _samplingDiskNumber = _settings.DiskNumber;
            _lastEventCount = 0;
            _lastEventGrowth = DateTime.Now;
            _lastBusyAt = DateTime.MinValue;
            _sourceRebuildCount = 0;
            lock (_stateSync)
            {
                _busyHistory.Clear();
                _highSince = null;
                _lowSince = null;
            }
            _log.Info(recreate
                ? Loc.F(LK.EngineMonitorSwitchedFormat, _settings.DiskNumber)
                : Loc.T(LK.EngineMonitorStarted));
        }
        catch (Exception ex)
        {
            _log.Error(recreate
                ? Loc.F(LK.EngineRebuildFailedFormat, ex.Message)
                : Loc.F(LK.EngineStartFailedFormat, ex.Message));
        }
    }

    /// <summary>
    /// 数据源活性检查：Windows 自己的内核会话会抢占内核提供程序，导致我们的 ETW 会话"在运行但收不到事件"，
    /// 此时重建数据源；连续多次重建无效则退回按字节占比的近似统计。
    /// </summary>
    private bool EnsureSourceAlive(IProcessIoSource source, double diskReadWriteBytesPerSec)
    {
        if (diskReadWriteBytesPerSec > 5 * Mega) _lastBusyAt = DateTime.Now;
        if (source is not EtwProcessIoSource etw) return true;

        long count = etw.EventCount;
        if (count > _lastEventCount)
        {
            _lastEventCount = count;
            _lastEventGrowth = DateTime.Now;
            _sourceRebuildCount = 0;
            return true;
        }

        bool stalled = (DateTime.Now - _lastEventGrowth).TotalSeconds > 12;
        bool diskActive = (DateTime.Now - _lastBusyAt).TotalSeconds < 12;
        if (!stalled || !diskActive) return true;

        _sourceRebuildCount++;
        _lastEventGrowth = DateTime.Now;
        _lastEventCount = 0;

        if (_sourceRebuildCount >= 3)
        {
            _sourceRebuildCount = 0;
            _log.Warn(Loc.T(LK.EngineApproxFallback));
            try
            {
                _ioSource?.Dispose();
                _ioSource = new IoCountersProcessIoSource();
            }
            catch (Exception ex)
            {
                _log.Error(Loc.F(LK.EngineApproxSwitchFailedFormat, ex.Message));
            }
            return false;
        }

        _log.Warn(Loc.F(LK.EngineEtwStalledFormat, _sourceRebuildCount));
        _restartRequested = true;
        return false;
    }

    public void Pause()
    {
        _paused = true;
        ReleaseTarget(Loc.T(LK.StatePaused));
        _log.Info(Loc.T(LK.EngineMonitorPaused));
    }

    public void Resume()
    {
        _paused = false;
        lock (_stateSync)
        {
            _highSince = null;
            _lowSince = null;
        }
        _log.Info(Loc.T(LK.EngineMonitorResumed));
    }

    public bool ManualThrottle(int pid, string name)
    {
        lock (_stateSync)
        {
            if (_target != null) ReleaseTarget(Loc.T(LK.EngineReasonManualSwitch));

            var handle = _throttler.ApplyLevel1(pid, name, _settings.LowerIoPriority, _settings.IoPriorityLevel, _settings.LowerCpuPriority);
            if (handle == null)
            {
                _log.Warn(Loc.F(LK.EngineManualThrottleFailedFormat, name, pid, ThrottleErrorText()));
                return false;
            }

            handle.IsManual = true;
            _target = handle;
            _hardLimitTarget = false;
            _targetLevel = 1;
            _levelAppliedAt = DateTime.Now;
            _currentCapBytesPerSec = (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
            _currentCapIops = DefaultCapIops;
            _capHoldLogged = false;
            _capFloorLogged = false;
            _log.Info(Loc.F(LK.EngineManualPriorityDoneFormat, name, pid));

            if (_settings.EnableRateCap)
            {
                long cap = (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
                if (_throttler.ApplyLevel2(handle, cap, DefaultCapIops))
                {
                    _targetLevel = 2;
                    _log.Info(Loc.F(LK.EngineManualCapDoneFormat, name, _settings.RateCapMBps) +
                              Loc.F(LK.EngineIopsCapSuffixFormat, DefaultCapIops));
                }
                else
                {
                    _log.Warn(Loc.F(LK.EngineCapApplyFailedFormat, _throttler.LastErrorText));
                }
            }

            AddRecord(handle);
            PersistRecords();
            return true;
        }
    }

    public void ReleaseTarget(string reason)
    {
        lock (_stateSync)
        {
            var target = _target;
            if (target == null) return;

            _throttler.Release(target);
            _log.Info(Loc.F(LK.EngineRestoredFormat, target.Name, target.Pid, reason));

            RemoveRecord(target.Pid);
            _target = null;
            _hardLimitTarget = false;
            _targetLevel = 0;
            _idleTargetTicks = 0;
            _pendingPid = -1;
            _pendingCount = 0;
            _busyHistory.Clear();
            _capHoldLogged = false;
            _capFloorLogged = false;
            _capIneffectiveLogged = false;
            _busyBeforeLastTighten = -1;
            _currentCapBytesPerSec = 0;
            _currentCapIops = 0;
            _targetOccupancyHistory.Clear();
            _targetBytesHistory.Clear();
            _targetIopsHistory.Clear();
        }
        PersistRecords();
    }

    private void AddRecord(ThrottleHandle handle)
    {
        lock (_recordSync)
        {
            _records.RemoveAll(r => r.Pid == handle.Pid);
            _records.Add(new ActiveThrottleRecord
            {
                Pid = handle.Pid,
                Name = handle.Name,
                AppliedIoPriority = handle.AppliedIoPriority,
                OriginalIoPriority = handle.OriginalIoPriority,
                AppliedPriorityClass = handle.AppliedPriorityClass,
                OriginalPriorityClass = handle.OriginalPriorityClass,
                StartedAtUtc = handle.StartedAt.ToUniversalTime()
            });
        }
    }

    private void RemoveRecord(int pid)
    {
        lock (_recordSync) _records.RemoveAll(r => r.Pid == pid);
    }

    private void RestoreStaleThrottles()
    {
        var stale = ActiveThrottleStore.Load();
        if (stale.Count == 0) return;

        foreach (var record in stale)
        {
            bool restored = ProcessThrottler.RestoreStale(
                record.Pid, record.AppliedIoPriority, record.OriginalIoPriority,
                record.AppliedPriorityClass, record.OriginalPriorityClass);

            if (restored)
                _log.Warn(Loc.F(LK.EngineRestoredStaleFormat, record.Name, record.Pid));
        }

        ActiveThrottleStore.Save(Array.Empty<ActiveThrottleRecord>());
    }

    private void PersistRecords()
    {
        // 状态文件只用于崩溃兜底：异步顺序写盘，避免在界面线程做文件 IO
        lock (_recordSync)
        {
            _pendingRecords = _records.ToList();
            if (_recordWriterRunning) return;
            _recordWriterRunning = true;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                List<ActiveThrottleRecord>? next;
                lock (_recordSync)
                {
                    next = _pendingRecords;
                    _pendingRecords = null;
                    if (next == null)
                    {
                        _recordWriterRunning = false;
                        return;
                    }
                }

                ActiveThrottleStore.Save(next);
            }
        });
    }

    private void WorkerLoop()
    {
        InitializeSources(recreate: false);
        _restartRequested = false;

        long last = Stopwatch.GetTimestamp();
        while (!_cts.IsCancellationRequested)
        {
            if (_restartRequested)
            {
                _restartRequested = false;
                ReleaseTarget(Loc.T(LK.EngineReasonSwitchDisk));
                InitializeSources(recreate: true);
            }
            else if ((_diskSampler == null || _ioSource == null) && DateTime.Now >= _nextInitRetry)
            {
                // 启动时初始化失败（例如 ETW 被占用、PDH 暂时不可用）时自动重试，避免"程序在跑但什么都没做"
                _nextInitRetry = DateTime.Now.AddSeconds(15);
                InitializeSources(recreate: false);
            }

            double elapsed = Stopwatch.GetElapsedTime(last).TotalSeconds;   // 距上一次采样，用于换算速率
            last = Stopwatch.GetTimestamp();

            // 注意：睡眠时间要用"本轮采样耗时"来算。若用 elapsed（它已包含上一轮睡眠），
            // 每次都会算出 0 并被钳到 200ms，采样会变成 2~5 次/秒，
            // 于是设置里的"秒"全部缩水、限速会反复抖动。
            double workMs;
            long tickStart = Stopwatch.GetTimestamp();
            try
            {
                Tick(elapsed);
            }
            catch (Exception ex)
            {
                _log.Error(Loc.F(LK.EngineSampleFailedFormat, ex.Message));
            }
            finally
            {
                workMs = Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
            }

            int sleep = (int)Math.Clamp(1000 - workMs, 200, 1000);
            if (_cts.Token.WaitHandle.WaitOne(sleep)) break;
        }
    }

    private void Tick(double elapsed)
    {
        long sampleStarted = Stopwatch.GetTimestamp();
        var sampler = _diskSampler;
        var source = _ioSource;
        if (sampler == null || source == null) return;

        // 数据源健康检查：ETW 会话意外中断时重建（5 分钟冷却）
        string health = source.HealthError;
        if (!string.IsNullOrEmpty(health) && DateTime.Now - _lastSourceRestart > TimeSpan.FromMinutes(5))
        {
            _lastSourceRestart = DateTime.Now;
            _log.Warn(Loc.F(LK.EngineEtwInterruptedFormat, health));
            _restartRequested = true;
            return;
        }

        sampler.Collect();
        var disk = sampler.GetDisk(_settings.DiskNumber);

        double diskThroughput = (disk?.ReadBytesPerSec ?? 0) + (disk?.WriteBytesPerSec ?? 0);
        if (!EnsureSourceAlive(source, diskThroughput)) return;

        var samples = source.Snapshot(elapsed);

        var rows = new List<ProcessIoRow>(samples.Count);
        bool namesRefreshed = false;
        foreach (var sample in samples)
        {
            if (sample.Pid <= 0 || sample.Pid == Environment.ProcessId) continue;

            string name;
            if (!string.IsNullOrEmpty(sample.Name))
            {
                name = sample.Name;
            }
            else
            {
                // ETW 数据源只给 PID：整体映射有 2 秒缓存，只有刚启动的进程才会单独查一次
                if (!namesRefreshed)
                {
                    ProcessUtil.GetProcessNames();
                    namesRefreshed = true;
                }

                name = ProcessUtil.ResolveProcessName(sample.Pid) ?? $"PID {sample.Pid}";
            }

            if (string.Equals(name, "DiskGuard", StringComparison.OrdinalIgnoreCase)) continue;

            rows.Add(new ProcessIoRow
            {
                Pid = sample.Pid,
                Name = name,
                ReadBytesPerSec = sample.ReadBytesPerSec,
                WriteBytesPerSec = sample.WriteBytesPerSec,
                CurrentBytesPerSec = sample.CurrentBytesPerSec,
                ServiceTimeMs = sample.ServiceTimeMs,
                IoCount = sample.IoCount,
                CurrentIoCount = sample.CurrentIoCount
            });
        }

        // 核心指标：该进程让磁盘忙碌的时间百分比
        ComputeOccupancy(rows, elapsed, disk?.BusyPercent ?? 0, source.ProvidesServiceTime);

        rows.Sort((a, b) =>
        {
            int byOccupancy = b.OccupancyPercent.CompareTo(a.OccupancyPercent);
            return byOccupancy != 0 ? byOccupancy : b.TotalBytesPerSec.CompareTo(a.TotalBytesPerSec);
        });

        var systemRow = rows.FirstOrDefault(r => r.Pid == 4);
        var now = DateTime.Now;
        double systemCpu = ReadSystemCpuPercent(now);

        // 下面这段会读写 _target / 历史窗口等共享状态，而界面线程的"暂停/还原/手动限速"也会改它们，
        // 因此统一放进 _stateSync：两个线程不会同时改限速目标，也不会重复释放同一个句柄。
        double busy = disk?.BusyPercent ?? 0;
        EngineSnapshot snapshot;
        lock (_stateSync)
        {
            var settings = _settings;
            // 被限速日志里带上"实际采样的 PDH 实例名 + 该磁盘当时的读写速率"，
            // 下次再出现"忙率与真实磁盘对不上"时，日志本身就足以定位（见 DiskSampler.GetDisk 的注释）
            _diskInfoSuffix = disk == null
                ? string.Empty
                : Loc.F(LK.EngineDiskInstanceSuffixFormat, disk.InstanceName,
                    ProcessUtil.FormatRate(disk.ReadBytesPerSec), ProcessUtil.FormatRate(disk.WriteBytesPerSec),
                    disk.LatencyValid ? $"{disk.LatencyMs:0.#} ms" : "?");
            int observedForegroundPid = ProcessUtil.GetForegroundProcessId();
            int foregroundPid = settings.ProtectForeground ? observedForegroundPid : 0;
            // 保护对象是"前台程序的整个进程族"：浏览器/Electron/商店应用往往是多进程，
            // 前台窗口和真正读写磁盘的不是同一个 PID，只保护单个 PID 会限速用户正在用的软件。
            HashSet<int> protectedPids = foregroundPid > 0
                ? ProcessUtil.GetProcessFamily(foregroundPid)
                : new HashSet<int>();
            int windowLength = WindowLength();

            UpdateRateHistory(rows, windowLength);

            // "忙率很高但没有任何实际吞吐、响应时间也正常"：多为 SSD 自身回收 / TRIM / 计数器异常。
            // 这种时候限速任何进程都改善不了磁盘忙率，只会让被限速的程序更卡，
            // 所以连续 3 秒如此就判定为"不可限速"，不做任何动作（并把已有的自动限速还原）。
            // 响应时间很长（磁盘真的在拖后腿）时不走这条路：那时候该照常挑目标限速。
            double diskBytes = (disk?.ReadBytesPerSec ?? 0) + (disk?.WriteBytesPerSec ?? 0);
            bool latencyKnown = disk?.LatencyValid == true;
            bool latencyFast = !latencyKnown || disk!.LatencyMs < BogusBusyLatencyMs;
            // ETW 精确绑定所选物理盘；PDH 偶发报 0 时，只要 ETW 仍看到该盘上的 IO，
            // 就不能把它当成 SSD 空转并撤销限速（近期日志出现过忙率 100%、ETW 98.4% 但 PDH 读写为 0）。
            bool preciseDiskActivity = source.IsPrecise && rows.Any(r => r.CurrentBytesPerSec > 0 || r.CurrentIoCount > 0);
            if (busy >= 90 && diskBytes < 64 * 1024 && (disk?.QueueLength ?? 0) < 0.5 && latencyFast && !preciseDiskActivity)
                _noThroughputTicks++;
            else _noThroughputTicks = 0;

            if (!_paused) RunStateMachine(busy, rows, foregroundPid, protectedPids, now);

            // 被限速的进程始终显示在列表最前面（即使这一秒它正处于限速等待、没有产生 IO）
            if (_target != null && !ContainsPid(rows, _target.Pid))
            {
                rows.Insert(0, new ProcessIoRow
                {
                    Pid = _target.Pid,
                    Name = _target.Name,
                    OccupancyPercent = 0
                });
            }

            foreach (var row in rows) row.Tag = Classify(row, protectedPids);

            var targetRate = 0.0;
            var targetOccupancy = 0.0;
            if (_target != null)
            {
                foreach (var row in rows)
                {
                    if (row.Pid != _target.Pid) continue;
                    targetRate = row.TotalBytesPerSec;
                    targetOccupancy = row.OccupancyPercent;
                    break;
                }
            }
            _lastTargetOccupancyPercent = targetOccupancy;

            snapshot = new EngineSnapshot
            {
                Timestamp = now,
                BusyPercent = busy,
                QueueLength = disk?.QueueLength ?? 0,
                ReadBytesPerSec = disk?.ReadBytesPerSec ?? 0,
                WriteBytesPerSec = disk?.WriteBytesPerSec ?? 0,
                LatencyMs = disk?.LatencyValid == true ? disk.LatencyMs : -1,
                Disks = sampler.Disks,
                DiskNumber = settings.DiskNumber,
                DiskLabel = disk?.DisplayName ?? Loc.F(LK.DiskLabelFormat, settings.DiskNumber),
                SelectedDiskAvailable = disk != null,
                SampleIntervalMs = elapsed * 1000,
                SampleDurationMs = Stopwatch.GetElapsedTime(sampleStarted).TotalMilliseconds,
                ForegroundPid = observedForegroundPid,
                IoEventCount = source is EtwProcessIoSource events ? events.EventCount : -1,
                IoHealthError = source.HealthError,
                Rows = rows.Count > 25 ? rows.GetRange(0, 25) : rows,
                ActivePid = _target?.Pid ?? -1,
                ActiveName = _target?.Name ?? string.Empty,
                ActiveLevel = _targetLevel,
                ActiveSince = _target?.StartedAt,
                ActiveRateBytesPerSec = targetRate,
                ActiveOccupancyPercent = targetOccupancy,
                ActiveCapBytesPerSec = _target?.CapBytesPerSec ?? 0,
                ActiveCapIops = _target?.CapIops ?? 0,
                StateKind = BuildStateKind(),
                StateText = BuildStateText(),
                Paused = _paused,
                IoMode = source.Mode,
                IoPrecise = source.IsPrecise,
                ShareMetric = source.ShareMetricName,
                TriggerPercent = settings.TriggerPercent,
                RecoverPercent = settings.RecoverPercent,
                TriggerOccupancyPercent = settings.TriggerOccupancyPercent,
                TargetOccupancyPercent = settings.TargetOccupancyPercent,
                SystemOccupancyPercent = systemRow?.OccupancyPercent ?? 0,
                SystemRateBytesPerSec = systemRow?.TotalBytesPerSec ?? 0,
                SystemIoCount = systemRow?.IoCount ?? 0,
                SystemCpuPercent = systemCpu,
                StartupAgeSeconds = Math.Max(0, (now - _bootAt).TotalSeconds)
            };
        }

        LastSnapshot = snapshot;
        SnapshotProduced?.Invoke(snapshot);

        _tickCount++;
        if (_debugDiagnostics && _tickCount % 5 == 0)
        {
            int eventCount = source is EtwProcessIoSource etw ? (int)Math.Min(int.MaxValue, etw.EventCount) : -1;
            string etwExtra = source is EtwProcessIoSource e2
                ? Loc.F(LK.EngineDiagnosticExtraFormat, e2.LastSnapshotItems, e2.DroppedDisk, e2.DroppedPid, e2.DroppedSize)
                : string.Empty;
            _log.Info(Loc.F(LK.EngineDiagnosticFormat, rows.Count, busy, eventCount, etwExtra, source.Mode));
        }
    }

    /// <summary>读取系统内核忙碌时间作为 PID 4 的 CPU 诊断近似值，不打开 PID 4 句柄。</summary>
    private double ReadSystemCpuPercent(DateTime now)
    {
        try
        {
            if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            long idleTicks = FileTimeTicks(idle);
            long kernelTicks = FileTimeTicks(kernel);
            long userTicks = FileTimeTicks(user);
            if (_lastSystemCpuAt == default)
            {
                _lastSystemIdleTicks = idleTicks;
                _lastSystemKernelTicks = kernelTicks;
                _lastSystemUserTicks = userTicks;
                _lastSystemCpuAt = now;
                return 0;
            }

            long idleDelta = Math.Max(0, idleTicks - _lastSystemIdleTicks);
            long kernelDelta = Math.Max(0, kernelTicks - _lastSystemKernelTicks);
            long userDelta = Math.Max(0, userTicks - _lastSystemUserTicks);
            long totalDelta = kernelDelta + userDelta;
            double percent = totalDelta > 0 ? Math.Max(0, kernelDelta - idleDelta) * 100.0 / totalDelta : 0;
            _lastSystemIdleTicks = idleTicks;
            _lastSystemKernelTicks = kernelTicks;
            _lastSystemUserTicks = userTicks;
            _lastSystemCpuAt = now;
            return Math.Clamp(percent, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private static long FileTimeTicks(System.Runtime.InteropServices.ComTypes.FILETIME value)
        => ((long)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    private bool _debugDiagnostics;
    private int _tickCount;

    private void RunStateMachine(double busy, List<ProcessIoRow> rows, int foregroundPid, HashSet<int> protectedPids, DateTime now)
    {
        int triggerSamples = Math.Clamp((int)Math.Round(_settings.TriggerSeconds), 1, 120);
        int recoverSamples = Math.Clamp((int)Math.Round(_settings.RecoverSeconds), 1, 120);

        PruneBlockedPids(now);

        // 用滑动窗口平均值判定，避免忙率在阈值附近波动时永远无法触发
        _busyHistory.Enqueue(busy);
        while (_busyHistory.Count > Math.Max(triggerSamples, recoverSamples)) _busyHistory.Dequeue();

        bool high = _busyHistory.Count >= triggerSamples && WindowAverage(triggerSamples) >= _settings.TriggerPercent;
        // 还原条件更严格：最近 recoverSamples 秒里最忙的一秒也不高于恢复阈值，避免刚限速就抖动还原
        bool low = _busyHistory.Count >= recoverSamples && WindowMax(recoverSamples) <= _settings.RecoverPercent;
        double minRate = _settings.MinRateMBps > 0 ? _settings.MinRateMBps * Mega : 0;

        if (high) { _lowSince = null; _highSince ??= now; }
        else if (low) { _highSince = null; _lowSince ??= now; }
        else { _highSince = null; _lowSince = null; }

        bool hardPressure = busy >= DiskBusyHardLimitPercent;

        // 见 Tick 里的说明：磁盘没有实际吞吐时不限速，已有自动限速也还原
        if (_noThroughputTicks >= 3)
        {
            if (_target != null && !_target.IsManual)
            {
                ReleaseTarget(Loc.T(LK.EngineReasonNoThroughput));
                return;
            }

            if (_target == null && now - _lastNoThroughputLog > TimeSpan.FromMinutes(1))
            {
                _lastNoThroughputLog = now;
                _log.Warn(Loc.F(LK.EngineBusyNoThroughputFormat, _settings.DiskNumber, busy) + _diskInfoSuffix);
            }

            WarnDefragIfRunning(now);
            return;
        }

        // 目标进程已退出
        if (_target != null && !ProcessAlive(_target.Pid))
        {
            var exited = _target;
            _log.Info(Loc.F(LK.EngineTargetExitedFormat, exited.Name, exited.Pid));
            _throttler.Release(exited);
            RemoveRecord(exited.Pid);
            _target = null;
            _hardLimitTarget = false;
            _targetLevel = 0;
            PersistRecords();
            // 目标退出后重新累积窗口，避免立即误伤无关进程
            _busyHistory.Clear();
            _targetOccupancyHistory.Clear();
            _targetBytesHistory.Clear();
            _targetIopsHistory.Clear();
            return;
        }

        if (_target != null)
        {
            if (!_target.IsManual)
            {
                if (hardPressure) _hardLimitTarget = true;

                // 被限速的程序一旦被用户加入保护名单，立即还原。
                if (_settings.IsWhitelisted(_target.Name))
                {
                    ReleaseTarget(Loc.T(LK.EngineReasonWhitelisted));
                    return;
                }

                // 硬上限期间即使目标成为前台也继续限速；降到安全区并稳定后再还原。
                // 普通负载下仍遵守前台保护，用户切回窗口即可立即恢复。
                if (!_hardLimitTarget && _settings.ProtectForeground && protectedPids.Contains(_target.Pid))
                {
                    ReleaseTarget(Loc.T(LK.EngineReasonForeground));
                    return;
                }

                // 紧急限速只需把磁盘忙率压到安全区，不必继续等待进程自身 IO 归零。
                int hardRecoverySamples = DiskBusyRecoverySamples;
                if (_hardLimitTarget && _busyHistory.Count >= hardRecoverySamples &&
                    WindowMax(hardRecoverySamples) <= DiskBusyRecoveryPercent)
                {
                    ReleaseTarget(Loc.F(LK.EngineReasonDiskRecoveredFormat,
                        _settings.DiskNumber, DiskBusyRecoveryPercent));
                    return;
                }

                ControlTarget(busy, rows, protectedPids, now, high, hardPressure);
            }
            return;
        }

        // 单盘忙率 98% 是硬上限，不受“紧急保护”开关和前台保护设置影响；
        // 用户把紧急阈值设得更低时，也可以更早进入保护。
        bool emergency = hardPressure || (_settings.EnableEmergencyThrottle && busy >= _settings.EmergencyPercent);
        if (emergency) _emergencyTicks++;
        else _emergencyTicks = 0;

        // 忙率到顶、磁盘却几乎没有实际读写、响应时间也正常时（SSD 自身回收 / TRIM / 计数器异常），
        // 限速任何进程都改善不了：这里直接不进入限速，避免"限速 → 1 秒后又还原"的空转。
        if (emergency && (hardPressure || _emergencyTicks >= 2) && _noThroughputTicks == 0)
        {
            // 达到硬上限时允许挑选前台进程，但仍只挑当前确实在产生磁盘 IO 的负载源；
            // 输入法、桌面关键进程和用户保护名单仍由 FindCandidate 内部硬保护。
            var emergencyProtectedPids = hardPressure ? NoProtectedPids : protectedPids;
            var urgent = FindCandidate(rows, emergencyProtectedPids, ignoreBlocked: false,
                minShareOverride: 5, minRate: 512 * 1024, minIopsOverride: 4);
            if (urgent == null)
            {
                // 第二级：低传输但高占用（大量小 IO、FAT/FUA 落盘、慢速外置盘）——
                // 允许真正的高频小 IO 负载入选。
                urgent = FindCandidate(rows, emergencyProtectedPids, ignoreBlocked: false,
                    minShareOverride: 5, minRate: 0, minIopsOverride: 1);
            }
            if (urgent != null)
            {
                _emergencyTicks = 0;
                if (hardPressure && protectedPids.Contains(urgent.Pid))
                {
                    _log.Warn(Loc.F(LK.EngineEmergencyForegroundFormat,
                        _settings.DiskNumber, busy, urgent.Name, urgent.Pid) + _diskInfoSuffix);
                }
                else
                {
                    _log.Warn(Loc.F(LK.EngineEmergencyThrottleFormat,
                        _settings.DiskNumber, busy,
                        hardPressure ? DiskBusyHardLimitPercent : _settings.EmergencyPercent,
                        urgent.Name, urgent.Pid) + _diskInfoSuffix);
                }
                Engage(urgent, busy, emergency: true, hardLimit: hardPressure);
                return;
            }
        }

        if (!high && !hardPressure) return;

        var candidate = FindCandidate(rows, protectedPids, ignoreBlocked: false, minRate: minRate);
        if (candidate == null)
        {
            if (!_warnedUnable)
            {
                _warnedUnable = true;
                // 这里报"持续偏高"，所以要用触发窗口的平均值：忙率从 100% 掉到 7% 的那一拍，
                // 瞬时值会让日志出现"忙率 7% 持续偏高"这种自相矛盾的记录。
                _log.Warn(Loc.F(LK.EngineBusyNoCandidateFormat,
                    _settings.DiskNumber, WindowAverage(triggerSamples),
                    _settings.TriggerOccupancyPercent) + _diskInfoSuffix);
                WarnDefragIfRunning(now);
            }
            return;
        }

        Engage(candidate, busy, emergency: hardPressure, hardLimit: hardPressure);
    }

    /// <summary>
    /// 闭环控制：以"占磁盘忙碌时间百分比"为达标标准，先降优先级，不达标就逐级收紧吞吐上限，
    /// 直到目标占比降到设定值以下（或触及下限）。
    /// </summary>
    private void ControlTarget(double busy, List<ProcessIoRow> rows, HashSet<int> protectedPids, DateTime now, bool high, bool hardPressure)
    {
        var target = _target!;
        double instantOccupancy = 0;
        double instantBytes = 0;
        double instantIops = 0;

        foreach (var row in rows)
        {
            if (row.Pid != target.Pid) continue;
            instantOccupancy = row.OccupancyPercent;
            instantBytes = row.TotalBytesPerSec;
            instantIops = row.IoCount;
            break;
        }

        // 被限速的进程可能某些秒完全不发 IO（等待落盘），因此用窗口平均判断活动量
        int window = WindowLength();
        Enqueue(_targetOccupancyHistory, instantOccupancy, window);
        Enqueue(_targetBytesHistory, instantBytes, window);
        Enqueue(_targetIopsHistory, instantIops, window);

        double share = Average(_targetOccupancyHistory);
        double bytes = Average(_targetBytesHistory);
        double iops = Average(_targetIopsHistory);

        // 策略：限速保持到该进程自己不再占用磁盘（约 30 秒无有效 IO）为止，
        // 否则"限速→磁盘变空闲→解除→又被占满"会来回抖动。
        if (bytes < 0.5 * Mega && iops < 2 && share < 2) _idleTargetTicks++;
        else _idleTargetTicks = 0;

        if (_idleTargetTicks >= 3)
        {
            ReleaseTarget(Loc.F(LK.EngineIdleReleaseFormat, target.Name, window));
            return;
        }

        // 出现"明显更重"的进程（占比达到 2 倍当前目标且超过触发线）时切换目标
        var candidateProtectedPids = hardPressure ? NoProtectedPids : protectedPids;
        var heavier = FindCandidate(rows, candidateProtectedPids, ignoreBlocked: false,
            minShareOverride: Math.Max(_settings.TriggerOccupancyPercent, share * 2), excludePid: target.Pid);
        if (heavier != null)
        {
            if (_pendingPid == heavier.Pid) _pendingCount++;
            else { _pendingPid = heavier.Pid; _pendingCount = 1; }

            if (_pendingCount >= 3)
            {
                _log.Info(Loc.F(LK.EngineHeavierTargetFormat,
                    heavier.Name, heavier.Pid, heavier.AverageOccupancyPercent));
                // 复用统一入口：还原旧目标、清掉记录与窗口，再对新目标限速
                bool maintainHardLimit = _hardLimitTarget || hardPressure;
                ReleaseTarget(Loc.T(LK.EngineReasonSwitchHeavier));
                Engage(heavier, busy, hardLimit: maintainHardLimit);
                return;
            }
        }
        else
        {
            _pendingPid = -1;
            _pendingCount = 0;
        }

        if (!high && !hardPressure) return;

        // 用忙率的滑动平均判"够不够用"：避免某一秒瞬时回落就停止收紧
        double busyAvg = WindowAverage(Math.Clamp((int)Math.Round(_settings.TriggerSeconds), 1, 120));

        // 够用即可：忙率一旦回落到 95% 以下就不再继续收紧，不必把程序一路压到最低档
        if (busyAvg < BusyReliefPercent && !hardPressure)
        {
            if (!_capHoldLogged)
            {
                _capHoldLogged = true;
                _log.Info(Loc.F(LK.EngineBusyReliefFormat,
                    _settings.DiskNumber, BusyReliefPercent, target.Name) + _diskInfoSuffix);
            }
            return;
        }

        // 已达标：维持在设定目标占用以下，同样不再继续收紧
        if (share <= _settings.TargetOccupancyPercent && !hardPressure)
        {
            if (!_capHoldLogged)
            {
                _capHoldLogged = true;
                _log.Info(Loc.F(LK.EngineTargetSatisfiedFormat,
                    target.Name, share, _settings.TargetOccupancyPercent));
            }
            return;
        }
        _capHoldLogged = false;

        if ((now - _levelAppliedAt).TotalSeconds < _settings.EscalateSeconds) return;

        // 上次收紧之后忙率没有实质改善（瓶颈不在这个进程，或者磁盘自己在忙）：
        // 再往下压既救不了磁盘，又会让程序越来越卡，保持当前上限即可。
        if (_busyBeforeLastTighten >= 0 && busyAvg > _busyBeforeLastTighten - BusyImprovementPercent && !hardPressure)
        {
            if (!_capIneffectiveLogged)
            {
                _capIneffectiveLogged = true;
                _log.Warn(Loc.F(LK.EngineCapIneffectiveFormat,
                    _busyBeforeLastTighten, busyAvg, target.Name) + _diskInfoSuffix);
            }
            return;
        }

        if (_targetLevel == 1)
        {
            if (!_settings.EnableRateCap && !_hardLimitTarget && !hardPressure)
            {
                if (!_capFloorLogged)
                {
                    _capFloorLogged = true;
                    _log.Warn(Loc.F(LK.EngineCapDisabledHintFormat, target.Name, share));
                }
                return;
            }

            long cap = _currentCapBytesPerSec > 0
                ? _currentCapBytesPerSec
                : (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
            long capIops = _currentCapIops > 0 ? _currentCapIops : DefaultCapIops;

            if (_throttler.ApplyLevel2(target, cap, capIops))
            {
                _targetLevel = 2;
                _currentCapIops = capIops;
                _levelAppliedAt = now;
                _busyBeforeLastTighten = busyAvg;
                _log.Info(Loc.F(LK.EngineCapAppliedFormat, target.Name, share, cap / Mega) +
                          Loc.F(LK.EngineIopsCapSuffixFormat, capIops));
                PersistRecords();
            }
            else
            {
                _targetLevel = 2;   // 标记已尝试，避免每秒重试
                _levelAppliedAt = now;
                _log.Warn(Loc.F(LK.EngineCapUnsupportedFormat, _throttler.LastErrorText));
            }
            return;
        }

        if (_targetLevel >= 2 && _targetLevel < 3)
        {
            long floor = (long)(2 * Mega);
            long currentIops = _currentCapIops > 0 ? _currentCapIops : DefaultCapIops;
            long next = Math.Max(floor, _currentCapBytesPerSec / 2);
            long nextIops = Math.Max(MinCapIops, currentIops / 2);

            if ((next < _currentCapBytesPerSec || nextIops < currentIops) &&
                _throttler.ApplyLevel2(target, next, nextIops))
            {
                _currentCapBytesPerSec = next;
                _currentCapIops = nextIops;
                _levelAppliedAt = now;
                _busyBeforeLastTighten = busyAvg;
                _log.Info(Loc.F(LK.EngineCapTightenedFormat, target.Name, share, next / Mega) +
                          Loc.F(LK.EngineIopsCapSuffixFormat, nextIops));
                return;
            }

            if (_settings.EnableSuspendMode && _throttler.StartLevel3(target, _settings.SuspendRunMs, _settings.SuspendPauseMs))
            {
                _targetLevel = 3;
                _levelAppliedAt = now;
                _log.Warn(Loc.F(LK.EngineSuspendEnabledFormat,
                    target.Name, share, _settings.SuspendRunMs, _settings.SuspendPauseMs));
                return;
            }

            if (!_capFloorLogged)
            {
                _capFloorLogged = true;
                _log.Warn(Loc.F(LK.EngineCapAtMinimumFormat,
                    target.Name, _currentCapBytesPerSec / Mega, share) +
                    Loc.F(LK.EngineIopsCapSuffixFormat, _currentCapIops));
            }
        }
    }

    /// <summary>最近 count 个忙率采样的平均值。</summary>
    private double WindowAverage(int count)
    {
        int take = Math.Min(count, _busyHistory.Count);
        if (take <= 0) return 0;

        double sum = 0;
        int index = 0;
        int skip = _busyHistory.Count - take;
        foreach (double value in _busyHistory)
        {
            if (index++ < skip) continue;
            sum += value;
        }

        return sum / take;
    }

    /// <summary>最近 count 个忙率采样中的最大值。</summary>
    private double WindowMax(int count)
    {
        int take = Math.Min(count, _busyHistory.Count);
        if (take <= 0) return 0;

        double max = 0;
        int index = 0;
        int skip = _busyHistory.Count - take;
        foreach (double value in _busyHistory)
        {
            if (index++ < skip) continue;
            if (value > max) max = value;
        }

        return max;
    }

    private int WindowLength()
        => Math.Clamp((int)Math.Max(Math.Max(_settings.TriggerSeconds, _settings.RecoverSeconds), 5), 3, 120);

    /// <summary>
    /// 计算每个进程"让磁盘忙碌的时间百分比"（0-100）：
    /// ETW 模式直接用磁盘服务时间 ÷ 经过时间；
    /// 无服务时间时用（该进程 IO 次数占比 × 磁盘忙率）近似。
    /// </summary>
    private static void ComputeOccupancy(List<ProcessIoRow> rows, double elapsedSeconds, double busyPercent, bool providesServiceTime)
    {
        double windowMs = Math.Max(200, elapsedSeconds * 1000);

        if (providesServiceTime)
        {
            foreach (var row in rows)
                row.OccupancyPercent = Math.Clamp(row.ServiceTimeMs / windowMs * 100.0, 0, 100);
            return;
        }

        double totalBytes = rows.Sum(r => r.TotalBytesPerSec);
        double busy = Math.Clamp(busyPercent, 0, 100);
        foreach (var row in rows)
        {
            // 无服务时间数据时，按字节占比 × 磁盘忙率 估算（对大批量读写准确，对小 IO 偏保守）
            row.OccupancyPercent = totalBytes > 0
                ? Math.Clamp(row.TotalBytesPerSec / totalBytes * busy, 0, 100)
                : 0;
        }
    }

    /// <summary>维护每个进程的窗口平均速率、平均占比与平均 IO 次数。</summary>
    private void UpdateRateHistory(List<ProcessIoRow> rows, int windowLength)
    {
        var active = _activePidScratch;
        active.Clear();
        foreach (var row in rows)
        {
            active.Add(row.Pid);
            if (!_rateHistory.TryGetValue(row.Pid, out var history))
            {
                history = new PidHistory();
                _rateHistory[row.Pid] = history;
            }

            Enqueue(history.Bytes, row.TotalBytesPerSec, windowLength);
            Enqueue(history.Share, row.OccupancyPercent, windowLength);
            Enqueue(history.Iops, row.IoCount, windowLength);

            row.AverageBytesPerSec = Average(history.Bytes);
            row.AverageOccupancyPercent = Average(history.Share);
            row.AverageIoCount = Average(history.Iops);
        }

        if (_rateHistory.Count > active.Count + 64)
        {
            _stalePidScratch.Clear();
            foreach (int pid in _rateHistory.Keys)
            {
                if (!active.Contains(pid)) _stalePidScratch.Add(pid);
            }

            foreach (int pid in _stalePidScratch) _rateHistory.Remove(pid);
        }
    }

    private static void Enqueue(Queue<double> queue, double value, int maxCount)
    {
        queue.Enqueue(value);
        while (queue.Count > maxCount) queue.Dequeue();
    }

    private static double Average(Queue<double> queue)
    {
        if (queue.Count == 0) return 0;
        double sum = 0;
        foreach (double value in queue) sum += value;
        return sum / queue.Count;
    }

    private void Engage(ProcessIoRow row, double busy, bool emergency = false, bool hardLimit = false)
    {
        var handle = _throttler.ApplyLevel1(row.Pid, row.Name, _settings.LowerIoPriority, _settings.IoPriorityLevel, _settings.LowerCpuPriority);
        if (handle == null)
        {
            _blockedPids[row.Pid] = DateTime.Now.AddSeconds(60);
            _log.Warn(Loc.F(LK.EngineThrottleFailedFormat, row.Name, row.Pid, ThrottleErrorText()));
            return;
        }

        _warnedUnable = false;
        _target = handle;
        _hardLimitTarget = hardLimit;
        _targetLevel = 1;
        _levelAppliedAt = DateTime.Now;
        _idleTargetTicks = 0;
        _pendingPid = -1;
        _pendingCount = 0;
        _busyHistory.Clear();
        _capHoldLogged = false;
        _capFloorLogged = false;
        _capIneffectiveLogged = false;
        _busyBeforeLastTighten = -1;
        _currentCapBytesPerSec = (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
        _currentCapIops = InitialIopsCapFor(row);
        _targetOccupancyHistory.Clear();
        _targetBytesHistory.Clear();
        _targetIopsHistory.Clear();
        // 用"限速前这一秒之前的窗口平均值"把窗口填满：
        // 否则刚限速的几秒会被当成"该进程已停止占用"，三五秒就解除限速，
        // 于是出现"限速 → 解除 → 又限速"的抖动。
        for (int i = 0; i < WindowLength(); i++)
        {
            _targetOccupancyHistory.Enqueue(row.AverageOccupancyPercent);
            _targetBytesHistory.Enqueue(row.AverageBytesPerSec);
            _targetIopsHistory.Enqueue(row.AverageIoCount);
        }

        AddRecord(handle);
        PersistRecords();

        _log.Info(Loc.F(LK.EngineAutoThrottleFormat,
            _settings.DiskNumber, busy, handle.Name, handle.Pid,
            row.AverageOccupancyPercent, ProcessUtil.FormatRate(row.TotalBytesPerSec),
            _settings.TargetOccupancyPercent) + _diskInfoSuffix);

        if (emergency && (_settings.EnableRateCap || hardLimit))
        {
            long cap = (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
            if (_throttler.ApplyLevel2(handle, cap, _currentCapIops))
            {
                _targetLevel = 2;
                _levelAppliedAt = DateTime.Now;
                _busyBeforeLastTighten = busy;
                _log.Warn(Loc.F(LK.EngineEmergencyCapFormat, handle.Name, _settings.RateCapMBps) +
                          Loc.F(LK.EngineIopsCapSuffixFormat, _currentCapIops));
            }
        }
    }

    /// <summary>按"占磁盘忙碌时间百分比"挑选可限速的目标进程。</summary>
    private ProcessIoRow? FindCandidate(List<ProcessIoRow> rows, HashSet<int> protectedPids, bool ignoreBlocked,
        double? minShareOverride = null, int excludePid = 0, double minRate = 0, double? minIopsOverride = null)
    {
        double minShare = minShareOverride ?? _settings.TriggerOccupancyPercent;
        double minIops = minIopsOverride ?? _settings.MinIops;
        DateTime now = DateTime.Now;

        foreach (var row in rows)
        {
            if (row.AverageOccupancyPercent < minShare) continue;
            if (minIops > 0 && row.AverageIoCount < minIops) continue;
            if (minRate > 0 && row.AverageBytesPerSec < minRate) continue;
            // 只压"真的在产生 IO"的进程：IO 量太小说明它只是被拖慢的受害者，而不是压住磁盘的元凶，
            // 压它不会让磁盘变快，只会让它更卡（详见 MinLoadBytesPerSec 的注释）。
            if (!IsRealLoad(row)) continue;
            if (excludePid != 0 && row.Pid == excludePid) continue;
            if (row.Pid <= 4) continue;
            if (row.Pid == Environment.ProcessId) continue;
            if (ProcessUtil.IsSystemCritical(row.Name)) continue;
            if (_settings.IsWhitelisted(row.Name)) continue;
            if (protectedPids.Contains(row.Pid)) continue;
            if (!ignoreBlocked && _blockedPids.TryGetValue(row.Pid, out var until) && until > now) continue;
            return row;
        }
        return null;
    }

    /// <summary>该进程当前的 IO 量是否足以"压住磁盘"（否则限速它没有意义）。</summary>
    private bool IsRealLoad(ProcessIoRow row)
    {
        double minIops = Math.Max(Math.Max(1, _settings.MinIops), MinLoadIops);

        // 历史窗口用于确认它通常是负载源；当前采样再确认它此刻仍在发 IO。
        // 只看滑动平均会让刚停止工作的进程在 5–10 秒内仍被选中，
        // 而磁盘响应变慢时服务时间又会把这种“受害者”排到榜首，限速它会连带卡住输入。
        bool recentLoad = row.AverageBytesPerSec >= MinLoadBytesPerSec || row.AverageIoCount >= minIops;
        bool currentLoad = row.CurrentBytesPerSec >= MinLoadBytesPerSec || row.CurrentIoCount >= minIops;
        return recentLoad && currentLoad;
    }

    private string Classify(ProcessIoRow row, HashSet<int> protectedPids)
    {
        if (_target != null && _target.Pid == row.Pid)
        {
            return _targetLevel switch
            {
                3 => Loc.T(LK.TagThrottledSuspend),
                2 => Loc.T(LK.TagThrottledCap),
                _ => Loc.T(LK.TagThrottledPriority)
            };
        }

        if (row.Pid == 4) return Loc.T(LK.TagSystemKernel);
        if (ProcessUtil.IsSystemCritical(row.Name)) return Loc.T(LK.TagSystemCritical);
        if (_settings.IsWhitelisted(row.Name)) return Loc.T(LK.TagWhitelist);
        if (protectedPids.Contains(row.Pid)) return Loc.T(LK.TagForeground);
        return string.Empty;
    }

    /// <summary>限速器最近一次错误文本；没有细节时给出“权限不足或进程受保护”。</summary>
    private string ThrottleErrorText() =>
        _throttler.LastErrorText.Length > 0 ? _throttler.LastErrorText : Loc.T(LK.ErrorPermissionOrProtected);

    /// <summary>
    /// Windows「优化驱动器」在跑（defrag.exe -c -h -o）：这类系统维护的 IO 无法通过限速根治，
    /// 但它是本机最常见的"长时间全系统卡顿"来源，值得在日志里明确点出来并给出解决办法。
    /// </summary>
    private void WarnDefragIfRunning(DateTime now)
    {
        if (now - _lastDefragHint < TimeSpan.FromMinutes(5)) return;

        bool running = false;
        try
        {
            var processes = Process.GetProcessesByName("defrag");
            running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
        }
        catch
        {
            // 查询失败就当没在跑
        }

        if (!running) return;

        _lastDefragHint = now;
        _log.Warn(Loc.F(LK.EngineDefragRunningFormat, _settings.DiskNumber) + _diskInfoSuffix);
    }

    /// <summary>目标进程是否仍然存在（列表里没有它的行不代表它已退出，可能只是这一秒没有 IO）。</summary>
    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsPid(List<ProcessIoRow> rows, int pid)
    {
        foreach (var row in rows)
        {
            if (row.Pid == pid) return true;
        }
        return false;
    }

    /// <summary>清理过期的"拒绝访问"缓存：PID 会被系统回收，长期运行不能让这张表无限增长。</summary>
    private void PruneBlockedPids(DateTime now)
    {
        if (_blockedPids.Count == 0 || now - _lastBlockedCleanup < TimeSpan.FromMinutes(1)) return;

        _lastBlockedCleanup = now;
        _stalePidScratch.Clear();
        foreach (var pair in _blockedPids)
        {
            if (pair.Value <= now) _stalePidScratch.Add(pair.Key);
        }

        foreach (int pid in _stalePidScratch) _blockedPids.Remove(pid);
    }

    private string BuildStateKind()
    {
        if (Paused) return "paused";
        if (_target != null) return "throttled";
        if (_highSince.HasValue) return "watch";
        return "idle";
    }

    private string BuildStateText()
    {
        if (Paused) return Loc.T(LK.StatePaused);
        if (_target != null)
        {
            string level = _targetLevel switch
            {
                3 => Loc.T(LK.StateLevelSuspend),
                2 => Loc.T(LK.StateLevelCap),
                _ => Loc.T(LK.StateLevelPriority)
            };
            string shareText = _lastTargetOccupancyPercent > 0
                ? Loc.F(LK.StateShareSuffixFormat, _lastTargetOccupancyPercent)
                : string.Empty;
            return Loc.F(LK.StateThrottledFormat, _target.Name, level, shareText);
        }
        if (_highSince.HasValue) return Loc.T(LK.StateWatching);
        return Loc.T(LK.StateMonitoring);
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        _cts.Dispose();
        _ioSource?.Dispose();
        _diskSampler?.Dispose();
    }
}
