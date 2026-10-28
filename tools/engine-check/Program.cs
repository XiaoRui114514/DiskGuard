using System.Reflection;
using System.Runtime.CompilerServices;
using DiskGuard.Core.Config;
using DiskGuard.Core.Engine;
using DiskGuard.Core.Logging;
using DiskGuard.Core.Monitoring;
using DiskGuard.Core.Throttling;

// Pure engine transitions only: no engine worker, PDH/ETW sessions or native process handles.
// Run with: dotnet run --project tools/engine-check/EngineCheck.csproj
int failures = 0;
Run("hard-limit release retains two recovery samples with one-second settings", CheckRecovery);
Run("repeated stalled ETW rebuilds eventually fall back", CheckStalledSource);
Run("new disk, fresh initialization and received events reset the retry budget", CheckRetryReset);
if (failures != 0) throw new InvalidOperationException($"{failures} engine check(s) failed.");
Console.WriteLine("All engine checks passed.");

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine("FAIL: " + name + ": " + ex.GetBaseException().Message);
    }
}

static void CheckRecovery()
{
    using var log = new AppLogger { WriteToFile = false };
    using var engine = new GuardEngine(new AppSettings
    {
        TriggerSeconds = 1, RecoverSeconds = 1, TriggerPercent = 100,
        ProtectForeground = false, EscalateSeconds = 600
    }, log);
    // Block the asynchronous state-file writer; the fixture never writes the user's recovery file.
    Set(engine, "_recordWriterRunning", true);
    Set(engine, "_target", new ThrottleHandle { Pid = Environment.ProcessId, Name = "engine-check" });
    Set(engine, "_hardLimitTarget", true);
    Set(engine, "_targetLevel", 1);
    Set(engine, "_levelAppliedAt", DateTime.Now);
    var rows = new List<ProcessIoRow>
    {
        new() { Pid = Environment.ProcessId, Name = "engine-check", ReadBytesPerSec = 10 * 1024 * 1024,
            OccupancyPercent = 50, IoCount = 200 }
    };
    Invoke(engine, "RunStateMachine", 95d, rows, 0, new HashSet<int>(), DateTime.Now);
    Require(Get<ThrottleHandle?>(engine, "_target") != null, "must retain the target after only one safe sample");
    Invoke(engine, "RunStateMachine", 95d, rows, 0, new HashSet<int>(), DateTime.Now.AddSeconds(1));
    Require(Get<ThrottleHandle?>(engine, "_target") == null, "must release after two safe samples");
}

static void CheckStalledSource()
{
    using var log = new AppLogger { WriteToFile = false };
    using var engine = new GuardEngine(new AppSettings(), log);
    // Allocate the concrete event counter without opening a Windows session. Only EventCount is read.
    var source = (EtwProcessIoSource)RuntimeHelpers.GetUninitializedObject(typeof(EtwProcessIoSource));
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        Set(engine, "_lastEventGrowth", DateTime.Now.AddSeconds(-13));
        Require(!(bool)Invoke(engine, "EnsureSourceAlive", source, 10d * 1024 * 1024)!,
            "stalled source must defer sampling");
        if (attempt < 3)
        {
            Require(Get<bool>(engine, "_restartRequested"), "stalled source must request a rebuild");
            Set(engine, "_restartRequested", false);
            // Same reset performed by InitializeSources after replacing a session.
            Invoke(engine, "ResetSourceTracking", 0, true);
        }
    }
    Require(Get<IProcessIoSource?>(engine, "_ioSource") is IoCountersProcessIoSource,
        "third stalled source must switch to counters instead of rebuilding forever");
}

static void CheckRetryReset()
{
    using var log = new AppLogger { WriteToFile = false };
    using var engine = new GuardEngine(new AppSettings(), log);
    Set(engine, "_sourceRebuildCount", 2);
    Invoke(engine, "ResetSourceTracking", 1, true);
    Require(Get<int>(engine, "_sourceRebuildCount") == 0, "switching disks must start a new retry budget");
    Set(engine, "_sourceRebuildCount", 2);
    Invoke(engine, "ResetSourceTracking", 1, false);
    Require(Get<int>(engine, "_sourceRebuildCount") == 0, "fresh initialization must reset retries");
    var source = (EtwProcessIoSource)RuntimeHelpers.GetUninitializedObject(typeof(EtwProcessIoSource));
    Set(source, "_eventCount", 1L);
    Set(engine, "_sourceRebuildCount", 2);
    Require((bool)Invoke(engine, "EnsureSourceAlive", source, 0d)!, "received events must allow sampling");
    Require(Get<int>(engine, "_sourceRebuildCount") == 0, "received events must reset retries");
}

static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(instance.GetType().Name, name);
static void Set(object instance, string name, object? value) => Field(instance, name).SetValue(instance, value);
static T Get<T>(object instance, string name) => (T)Field(instance, name).GetValue(instance)!;
static object? Invoke(object instance, string name, params object[] args) =>
    (instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(instance.GetType().Name, name)).Invoke(instance, args);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
