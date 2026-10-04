using System.Text.Json;
using DiskGuard.Core.Config;
using DiskGuard.Core.Engine;
using DiskGuard.Core.Logging;
using DiskGuard.Core.Monitoring;

string root = Path.Combine(Path.GetTempPath(), "DiskGuard-diagnostics-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var time = new ManualClock(DateTimeOffset.Parse("2026-10-04T10:00:00+08:00"));
    var settings = new AppSettings { DeveloperMode = false, WriteLogFile = false };
    using var logger = new AppLogger { WriteToFile = false };
    string folder = Path.Combine(root, "records");
    using (var recorder = new DeveloperRecorder(logger, folder, "1.7.2", settings, time))
    {
        var disks = new[]
        {
            new DiskStatus { DiskNumber = 0, InstanceName = "0 C:", BusyPercent = 99, LatencyValid = true, LatencySeconds = .123 },
            new DiskStatus { DiskNumber = 1, InstanceName = "1 D:", BusyPercent = 41 },
            new DiskStatus { DiskNumber = -1, InstanceName = "_Total", BusyPercent = 100 }
        };
        recorder.RecordSnapshot(Sample(disks));
        disks[0].BusyPercent = 1;
        await recorder.FlushAsync();
        Check(!Directory.Exists(folder), "disabled mode does not write files");

        time.Advance(TimeSpan.FromSeconds(30));
        settings.DeveloperMode = true;
        recorder.Configure(settings);
        await recorder.FlushAsync();
        using (var first = JsonDocument.Parse(recorder.GetRecent().Single(e => e.Kind == "sample").ToJson()))
        {
            var data = first.RootElement.GetProperty("data");
            Check(data.GetProperty("disks").GetArrayLength() == 2, "individual physical disks; no _Total");
            Check(data.GetProperty("disks")[0].GetProperty("busyPercent").GetDouble() == 99, "history values survive sampler object reuse");
            Check(data.GetProperty("disks")[1].GetProperty("latencyMs").ValueKind == JsonValueKind.Null, "unknown latency stays unknown");
            Check(data.GetProperty("processScope").GetString() == "allProcessIoApproximation", "fallback process scope is explicit");
        }
        Check(ReadRecords(folder).Any(r => r.Time == time.GetUtcNow() - TimeSpan.FromSeconds(30) && r.Kind == "sample"), "enabling backfills launch cache");

        // 00:30 的数据在 10:10:45 查看时也必须被裁掉，不能只删除整个分钟文件。
        recorder.RecordSnapshot(Sample(disks));
        time.Advance(TimeSpan.FromSeconds(20));
        recorder.RecordSnapshot(Sample(disks));
        time.Advance(TimeSpan.FromSeconds(10));
        recorder.RecordSnapshot(Sample(disks));
        await recorder.FlushAsync();
        time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(45));
        recorder.RecordSnapshot(Sample(disks));
        await recorder.FlushAsync();
        AssertWindow(recorder, folder, time.GetUtcNow());
        var oldestMinute = File.ReadAllLines(Path.Combine(folder, "20261004-0200.jsonl"));
        Check(oldestMinute.Length == 1, "oldest minute is trimmed by timestamp, not retained in full");

        time.Advance(TimeSpan.FromSeconds(15)); // 10:11:00，保留恰好 10:01:00 这一条。
        recorder.RecordSnapshot(Sample(disks));
        await recorder.FlushAsync();
        AssertWindow(recorder, folder, time.GetUtcNow());
        Check(ReadRecords(folder).Any(r => r.Time == time.GetUtcNow() - TimeSpan.FromMinutes(10)), "exact ten-minute boundary retained");

        settings.DeveloperMode = false;
        recorder.Configure(settings);
        await recorder.FlushAsync();
        int count = ReadRecords(folder).Count;
        time.Advance(TimeSpan.FromSeconds(10));
        recorder.RecordSnapshot(Sample(disks));
        await recorder.FlushAsync();
        Check(ReadRecords(folder).Count <= count, "turning off stops disk recording");
        Check(recorder.GetRecent().Count == 0, "viewer hides disabled records");
        settings.DeveloperMode = true;
        recorder.Configure(settings);
        await recorder.FlushAsync();
        Check(ReadRecords(folder).Any(r => r.Time == time.GetUtcNow() && r.Kind == "sample"), "re-enabling backfills bounded cache");

        string settingsPath = Path.Combine(root, "settings.json");
        settings.Save(settingsPath);
        Check(AppSettings.Load(settingsPath).DeveloperMode, "developer mode persists through settings reload");
        File.WriteAllText(settingsPath, "{}");
        Check(!AppSettings.Load(settingsPath).DeveloperMode, "old configuration defaults to off");

        // 日志风暴不能无限占用内存；采样间断时仍清理旧数据。
        for (int i = 0; i < 3000; i++) logger.Info("diagnostic event " + i);
        Check(recorder.GetRecent().Count <= 2400, "record buffer has a capacity ceiling");
        time.Advance(TimeSpan.FromMinutes(11));
        await recorder.FlushAsync();
        Check(recorder.GetRecent().Count == 0 && ReadRecords(folder).Count == 0, "expiry runs even without new samples");

        // 写入错误不得卡住监控或丢失内存数据，目录恢复后应能重试。
        string blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        using var failing = new DeveloperRecorder(logger, blocked, "1.7.2", settings, time);
        failing.RecordSnapshot(Sample(disks));
        await failing.FlushAsync();
        Check(failing.LastError != null && failing.GetRecent().Any(r => r.Kind == "sample"), "write failure remains visible with memory data");
        File.Delete(blocked);
        await failing.FlushAsync();
        Check(failing.LastError == null && ReadRecords(blocked).Any(r => r.Kind == "sample"), "write failure recovers without restarting");
    }

    // 下次启动（包括默认关闭时）清理上次会话留下的过期数据。
    settings.DeveloperMode = true;
    using (var beforeRestart = new DeveloperRecorder(logger, folder, "1.7.2", settings, time))
    {
        beforeRestart.RecordSnapshot(Sample(new[] { new DiskStatus { DiskNumber = 0, BusyPercent = 80 } }));
        await beforeRestart.FlushAsync();
    }
    time.Advance(TimeSpan.FromMinutes(11));
    settings.DeveloperMode = false;
    using (var restarted = new DeveloperRecorder(logger, folder, "1.7.2", settings, time))
    {
        await restarted.FlushAsync();
        Check(ReadRecords(folder).Count == 0, "restart cleans expired previous-session files while disabled");
    }
    Console.WriteLine("All diagnostics checks passed.");
}
finally
{
    // 只删除本次创建的临时测试目录。
    Directory.Delete(root, recursive: true);
}

static EngineSnapshot Sample(IReadOnlyList<DiskStatus> disks) => new()
{
    DiskNumber = 0, SelectedDiskAvailable = true, Disks = disks,
    SampleIntervalMs = 1000, SampleDurationMs = 5, IoPrecise = false
};

static void AssertWindow(DeveloperRecorder recorder, string folder, DateTimeOffset now)
{
    Check(recorder.GetRecent().All(e => e.TimestampUtc >= now - DeveloperRecorder.Retention && e.TimestampUtc <= now), "memory obeys sliding ten-minute window");
    Check(ReadRecords(folder).All(e => e.Time >= now - DeveloperRecorder.Retention && e.Time <= now), "files obey sliding ten-minute window");
    Check(recorder.LastError == null, "background file writer succeeds");
}

static List<(DateTimeOffset Time, string Kind)> ReadRecords(string folder)
{
    if (!Directory.Exists(folder)) return new();
    return Directory.GetFiles(folder, "*.jsonl").SelectMany(File.ReadLines).Select(line =>
    {
        using var json = JsonDocument.Parse(line);
        return (json.RootElement.GetProperty("timestampUtc").GetDateTimeOffset(), json.RootElement.GetProperty("kind").GetString()!);
    }).ToList();
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}
