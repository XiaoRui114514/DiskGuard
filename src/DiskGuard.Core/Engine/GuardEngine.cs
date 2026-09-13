using System.Diagnostics;
using DiskGuard.Core.Config;
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
    private bool _capHoldLogged;
    private bool _capFloorLogged;
    private double _lastTargetOccupancyPercent;
    private DateTime _lastSourceRestart = DateTime.MinValue;
    private long _lastEventCount;
    private DateTime _lastEventGrowth = DateTime.Now;
    private DateTime _lastBusyAt = DateTime.MinValue;
    private int _sourceRebuildCount;
    private int _emergencyTicks;
    private DateTime _lastBlockedCleanup = DateTime.MinValue;

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
    public string IoMode => _ioSource?.Mode ?? "启动中…";
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
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DiskGuard-Engine" };
        _worker.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _worker?.Join(TimeSpan.FromSeconds(3)); } catch { }
        _worker = null;
        ReleaseTarget("监控已停止");
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
            _log.Info(recreate ? $"已切换到磁盘 {_settings.DiskNumber} 的统计（原有采样窗口已重置）。" : "监控已启动。");
        }
        catch (Exception ex)
        {
            _log.Error((recreate ? "重建数据源失败：" : "启动监控失败：") + ex.Message);
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
            _log.Warn("多次重建后 ETW 仍收不到事件，改用按字节占比的近似统计（磁盘忙率仍然准确）。");
            try
            {
                _ioSource?.Dispose();
                _ioSource = new IoCountersProcessIoSource();
            }
            catch (Exception ex)
            {
                _log.Error("切换近似统计失败：" + ex.Message);
            }
            return false;
        }

        _log.Warn($"ETW 已停止上报事件（多由系统内核会话抢占导致），正在重建数据源（第 {_sourceRebuildCount} 次）…");
        _restartRequested = true;
        return false;
    }

    public void Pause()
    {
        _paused = true;
        ReleaseTarget("已暂停监控");
        _log.Info("已暂停监控并解除全部限速。");
    }

    public void Resume()
    {
        _paused = false;
        lock (_stateSync)
        {
            _highSince = null;
            _lowSince = null;
        }
        _log.Info("已恢复监控。");
    }

    public bool ManualThrottle(int pid, string name)
    {
        lock (_stateSync)
        {
            if (_target != null) ReleaseTarget("切换手动限速目标");

            var handle = _throttler.ApplyLevel1(pid, name, _settings.LowerIoPriority, _settings.IoPriorityLevel, _settings.LowerCpuPriority);
            if (handle == null)
            {
                _log.Warn($"手动限速失败：{name} (PID {pid})" + (_throttler.LastErrorText.Length > 0 ? "，" + _throttler.LastErrorText : "，权限不足或进程受保护"));
                return false;
            }

            handle.IsManual = true;
            _target = handle;
            _targetLevel = 1;
            _levelAppliedAt = DateTime.Now;
            _currentCapBytesPerSec = (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
            _capHoldLogged = false;
            _capFloorLogged = false;
            _log.Info($"手动限速：{name} (PID {pid}) 已降到最低磁盘/CPU 优先级。");

            if (_settings.EnableRateCap)
            {
                long cap = (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
                if (_throttler.ApplyLevel2(handle, cap))
                {
                    _targetLevel = 2;
                    _log.Info($"手动限速：{name} 已施加磁盘吞吐上限 {_settings.RateCapMBps:0.#} MB/s。");
                }
                else
                {
                    _log.Warn($"未能施加磁盘吞吐上限（{_throttler.LastErrorText}），已保留优先级限速。");
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
            _log.Info($"已还原 {target.Name} (PID {target.Pid}) 的磁盘/CPU 优先级。（{reason}）");

            RemoveRecord(target.Pid);
            _target = null;
            _targetLevel = 0;
            _idleTargetTicks = 0;
            _pendingPid = -1;
            _pendingCount = 0;
            _busyHistory.Clear();
            _capHoldLogged = false;
            _capFloorLogged = false;
            _currentCapBytesPerSec = 0;
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
                _log.Warn($"发现上次异常退出残留的限速，已还原：{record.Name} (PID {record.Pid})");
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
                ReleaseTarget("切换监控磁盘");
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
                _log.Error("采样失败：" + ex.Message);
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
        var sampler = _diskSampler;
        var source = _ioSource;
        if (sampler == null || source == null) return;

        // 数据源健康检查：ETW 会话意外中断时重建（5 分钟冷却）
        string health = source.HealthError;
        if (!string.IsNullOrEmpty(health) && DateTime.Now - _lastSourceRestart > TimeSpan.FromMinutes(5))
        {
            _lastSourceRestart = DateTime.Now;
            _log.Warn($"ETW 统计中断：{health}，正在重建数据源…");
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
                ServiceTimeMs = sample.ServiceTimeMs,
                IoCount = sample.IoCount
            });
        }

        // 核心指标：该进程让磁盘忙碌的时间百分比
        ComputeOccupancy(rows, elapsed, disk?.BusyPercent ?? 0, source.ProvidesServiceTime);

        rows.Sort((a, b) =>
        {
            int byOccupancy = b.OccupancyPercent.CompareTo(a.OccupancyPercent);
            return byOccupancy != 0 ? byOccupancy : b.TotalBytesPerSec.CompareTo(a.TotalBytesPerSec);
        });

        // 下面这段会读写 _target / 历史窗口等共享状态，而界面线程的"暂停/还原/手动限速"也会改它们，
        // 因此统一放进 _stateSync：两个线程不会同时改限速目标，也不会重复释放同一个句柄。
        double busy = disk?.BusyPercent ?? 0;
        EngineSnapshot snapshot;
        lock (_stateSync)
        {
            var settings = _settings;
            int foregroundPid = settings.ProtectForeground ? ProcessUtil.GetForegroundProcessId() : 0;
            // 保护对象是"前台程序的整个进程族"：浏览器/Electron/商店应用往往是多进程，
            // 前台窗口和真正读写磁盘的不是同一个 PID，只保护单个 PID 会限速用户正在用的软件。
            HashSet<int> protectedPids = foregroundPid > 0
                ? ProcessUtil.GetProcessFamily(foregroundPid)
                : new HashSet<int>();
            var now = DateTime.Now;
            int windowLength = WindowLength();

            UpdateRateHistory(rows, windowLength);

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
                Disks = sampler.Disks,
                DiskNumber = settings.DiskNumber,
                DiskLabel = disk?.DisplayName ?? $"磁盘 {settings.DiskNumber}",
                Rows = rows.Count > 25 ? rows.GetRange(0, 25) : rows,
                ActivePid = _target?.Pid ?? -1,
                ActiveName = _target?.Name ?? string.Empty,
                ActiveLevel = _targetLevel,
                ActiveSince = _target?.StartedAt,
                ActiveRateBytesPerSec = targetRate,
                ActiveOccupancyPercent = targetOccupancy,
                ActiveCapBytesPerSec = _target?.CapBytesPerSec ?? 0,
                StateKind = BuildStateKind(),
                StateText = BuildStateText(),
                Paused = _paused,
                IoMode = source.Mode,
                IoPrecise = source.IsPrecise,
                ShareMetric = source.ShareMetricName,
                TriggerPercent = settings.TriggerPercent,
                RecoverPercent = settings.RecoverPercent,
                TriggerOccupancyPercent = settings.TriggerOccupancyPercent,
                TargetOccupancyPercent = settings.TargetOccupancyPercent
            };
        }

        LastSnapshot = snapshot;
        SnapshotProduced?.Invoke(snapshot);

        _tickCount++;
        if (_debugDiagnostics && _tickCount % 5 == 0)
        {
            int eventCount = source is EtwProcessIoSource etw ? (int)Math.Min(int.MaxValue, etw.EventCount) : -1;
            string etwExtra = source is EtwProcessIoSource e2
                ? $" 快照条数={e2.LastSnapshotItems} 丢弃(磁盘/进程/长度)={e2.DroppedDisk}/{e2.DroppedPid}/{e2.DroppedSize}"
                : string.Empty;
            _log.Info($"[诊断] 采样行数={rows.Count} 忙率={busy:0.0}% ETW事件累计={eventCount}{etwExtra} 数据源={source.Mode}");
        }
    }

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

        // 目标进程已退出
        if (_target != null && !ProcessAlive(_target.Pid))
        {
            var exited = _target;
            _log.Info($"被限速进程 {exited.Name} (PID {exited.Pid}) 已退出。");
            _throttler.Release(exited);
            RemoveRecord(exited.Pid);
            _target = null;
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
                // 被限速的程序一旦变成前台程序，或者被用户加入保护名单，立即还原：
                // 否则用户切过去发现"打字卡、点不动"，而它要等近 30 秒无 IO 才会解除。
                if (_settings.ProtectForeground && protectedPids.Contains(_target.Pid))
                {
                    ReleaseTarget("该进程已成为前台程序（保护前台）");
                    return;
                }

                if (_settings.IsWhitelisted(_target.Name))
                {
                    ReleaseTarget("该进程已在保护名单中");
                    return;
                }

                ControlTarget(busy, rows, protectedPids, now, high);
            }
            return;
        }

        // 紧急保护：忙率到顶时直接限速（不等 5 秒窗口、不校验占比），用于防止电脑突然卡死
        bool emergency = _settings.EnableEmergencyThrottle && busy >= _settings.EmergencyPercent;
        if (emergency) _emergencyTicks++;
        else _emergencyTicks = 0;

        if (emergency && _emergencyTicks >= 2)
        {
            var urgent = FindCandidate(rows, protectedPids, ignoreBlocked: false,
                minShareOverride: 5, minRate: Math.Max(minRate, 512 * 1024));
            if (urgent != null)
            {
                _emergencyTicks = 0;
                _log.Warn($"磁盘 {_settings.DiskNumber} 忙率 {busy:0}% 达到紧急阈值 {_settings.EmergencyPercent:0}%，立即限速 {urgent.Name}（PID {urgent.Pid}）。");
                Engage(urgent, busy, emergency: true);
                return;
            }
        }

        if (!high) return;

        var candidate = FindCandidate(rows, protectedPids, ignoreBlocked: false, minRate: minRate);
        if (candidate == null)
        {
            if (!_warnedUnable)
            {
                _warnedUnable = true;
                _log.Warn($"磁盘 {_settings.DiskNumber} 忙率 {busy:0}% 持续偏高，但没有磁盘占用超过 {_settings.TriggerOccupancyPercent:0.#}% 的可限速进程" +
                          "（可能由内核/驱动 IO、SSD 自身回收或大量分散的小 IO 引起）。");
            }
            return;
        }

        Engage(candidate, busy);
    }

    /// <summary>
    /// 闭环控制：以"占磁盘忙碌时间百分比"为达标标准，先降优先级，不达标就逐级收紧吞吐上限，
    /// 直到目标占比降到设定值以下（或触及下限）。
    /// </summary>
    private void ControlTarget(double busy, List<ProcessIoRow> rows, HashSet<int> protectedPids, DateTime now, bool high)
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
            ReleaseTarget($"被限速进程 {target.Name} 已停止磁盘占用（{window} 秒窗口平均活动接近 0）");
            return;
        }

        // 出现"明显更重"的进程（占比达到 2 倍当前目标且超过触发线）时切换目标
        var heavier = FindCandidate(rows, protectedPids, ignoreBlocked: false,
            minShareOverride: Math.Max(_settings.TriggerOccupancyPercent, share * 2), excludePid: target.Pid);
        if (heavier != null)
        {
            if (_pendingPid == heavier.Pid) _pendingCount++;
            else { _pendingPid = heavier.Pid; _pendingCount = 1; }

            if (_pendingCount >= 3)
            {
                _log.Info($"检测到占用更重的进程：{heavier.Name} (PID {heavier.Pid}) 磁盘占用 {heavier.AverageOccupancyPercent:0.#}%，切换限速目标。");
                // 复用统一入口：还原旧目标、清掉记录与窗口，再对新目标限速
                ReleaseTarget("切换到占用更高的进程");
                Engage(heavier, busy);
                return;
            }
        }
        else
        {
            _pendingPid = -1;
            _pendingCount = 0;
        }

        if (!high) return;

        // 已达标：维持当前限速，不再继续收紧
        if (share <= _settings.TargetOccupancyPercent)
        {
            if (!_capHoldLogged)
            {
                _capHoldLogged = true;
                _log.Info($"{target.Name} 的磁盘占用已降到 {share:0.#}%（达标线 {_settings.TargetOccupancyPercent:0.#}%），维持当前限速。");
            }
            return;
        }
        _capHoldLogged = false;

        if ((now - _levelAppliedAt).TotalSeconds < _settings.EscalateSeconds) return;

        if (_targetLevel == 1)
        {
            if (!_settings.EnableRateCap)
            {
                if (!_capFloorLogged)
                {
                    _capFloorLogged = true;
                    _log.Warn($"{target.Name} 仍占磁盘 {share:0.#}%，但未启用吞吐上限；可在设置中开启或启用强力模式。");
                }
                return;
            }

            long cap = _currentCapBytesPerSec > 0
                ? _currentCapBytesPerSec
                : (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;

            if (_throttler.ApplyLevel2(target, cap))
            {
                _targetLevel = 2;
                _levelAppliedAt = now;
                _log.Info($"{target.Name} 仍占磁盘 {share:0.#}%，施加磁盘吞吐上限 {cap / Mega:0.#} MB/s。");
                PersistRecords();
            }
            else
            {
                _targetLevel = 2;   // 标记已尝试，避免每秒重试
                _levelAppliedAt = now;
                _log.Warn($"该进程无法使用吞吐上限（{_throttler.LastErrorText}），继续使用优先级限速。");
            }
            return;
        }

        if (_targetLevel >= 2 && _targetLevel < 3)
        {
            long floor = (long)(2 * Mega);
            long next = Math.Max(floor, _currentCapBytesPerSec / 2);

            if (next < _currentCapBytesPerSec && _throttler.ApplyLevel2(target, next))
            {
                _currentCapBytesPerSec = next;
                _levelAppliedAt = now;
                _log.Info($"{target.Name} 仍占磁盘 {share:0.#}%，吞吐上限收紧到 {next / Mega:0.#} MB/s。");
                return;
            }

            if (_settings.EnableSuspendMode && _throttler.StartLevel3(target, _settings.SuspendRunMs, _settings.SuspendPauseMs))
            {
                _targetLevel = 3;
                _levelAppliedAt = now;
                _log.Warn($"{target.Name} 已到吞吐下限仍占磁盘 {share:0.#}%，启用间歇挂起（{_settings.SuspendRunMs}ms 运行 / {_settings.SuspendPauseMs}ms 挂起）。");
                return;
            }

            if (!_capFloorLogged)
            {
                _capFloorLogged = true;
                _log.Warn($"{target.Name} 已到吞吐下限（{_currentCapBytesPerSec / Mega:0.#} MB/s），当前占磁盘 {share:0.#}%；" +
                          "如仍卡顿，可在设置中开启强力模式。");
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

    private void Engage(ProcessIoRow row, double busy, bool emergency = false)
    {
        var handle = _throttler.ApplyLevel1(row.Pid, row.Name, _settings.LowerIoPriority, _settings.IoPriorityLevel, _settings.LowerCpuPriority);
        if (handle == null)
        {
            _blockedPids[row.Pid] = DateTime.Now.AddSeconds(60);
            _log.Warn($"无法限速 {row.Name} (PID {row.Pid})：" + (_throttler.LastErrorText.Length > 0 ? _throttler.LastErrorText : "权限不足或进程受保护"));
            return;
        }

        _warnedUnable = false;
        _target = handle;
        _targetLevel = 1;
        _levelAppliedAt = DateTime.Now;
        _idleTargetTicks = 0;
        _pendingPid = -1;
        _pendingCount = 0;
        _busyHistory.Clear();
        _capHoldLogged = false;
        _capFloorLogged = false;
        _currentCapBytesPerSec = (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
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

        _log.Info($"磁盘 {_settings.DiskNumber} 忙率 {busy:0}%，已限速最高占用进程 {handle.Name} (PID {handle.Pid})：" +
                  $"磁盘占用 {row.AverageOccupancyPercent:0.#}%（{ProcessUtil.FormatRate(row.TotalBytesPerSec)}，目标降到 {_settings.TargetOccupancyPercent:0.#}% 以下）→ 磁盘优先级降到最低。");

        if (emergency && _settings.EnableRateCap)
        {
            long cap = (long)Math.Max(1, _settings.RateCapMBps) * (long)Mega;
            if (_throttler.ApplyLevel2(handle, cap))
            {
                _targetLevel = 2;
                _levelAppliedAt = DateTime.Now;
                _log.Warn($"紧急模式：{handle.Name} 已直接施加磁盘吞吐上限 {_settings.RateCapMBps:0.#} MB/s。");
            }
        }
    }

    /// <summary>按"占磁盘忙碌时间百分比"挑选可限速的目标进程。</summary>
    private ProcessIoRow? FindCandidate(List<ProcessIoRow> rows, HashSet<int> protectedPids, bool ignoreBlocked,
        double? minShareOverride = null, int excludePid = 0, double minRate = 0)
    {
        double minShare = minShareOverride ?? _settings.TriggerOccupancyPercent;
        double minIops = _settings.MinIops;
       DateTime now = DateTime.Now;

        foreach (var row in rows)
        {
            if (row.AverageOccupancyPercent < minShare) continue;
            if (minIops > 0 && row.AverageIoCount < minIops) continue;
            if (minRate > 0 && row.AverageBytesPerSec < minRate) continue;
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

    private string Classify(ProcessIoRow row, HashSet<int> protectedPids)
    {
        if (_target != null && _target.Pid == row.Pid)
        {
            return _targetLevel switch
            {
                3 => "已限速·挂起",
                2 => "已限速·吞吐上限",
                _ => "已限速·优先级"
            };
        }

        if (row.Pid == 4) return "系统内核";
        if (ProcessUtil.IsSystemCritical(row.Name)) return "系统关键";
        if (_settings.IsWhitelisted(row.Name)) return "保护名单";
        if (protectedPids.Contains(row.Pid)) return "前台程序";
        return string.Empty;
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
        if (Paused) return "已暂停";
        if (_target != null)
        {
            string level = _targetLevel switch
            {
                3 => "间歇挂起",
                2 => "吞吐上限",
                _ => "低优先级"
            };
            string shareText = _lastTargetOccupancyPercent > 0 ? $"，占磁盘 {_lastTargetOccupancyPercent:0.#}%" : string.Empty;
            return $"已限速：{_target.Name}（{level}{shareText}）";
        }
        if (_highSince.HasValue) return "磁盘繁忙，正在观察…";
        return "监控中";
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        _cts.Dispose();
        _ioSource?.Dispose();
        _diskSampler?.Dispose();
    }
}
