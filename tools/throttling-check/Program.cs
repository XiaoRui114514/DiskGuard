extern alias production;

using System.Reflection;
using DiskGuard.Core.Interop;
using DiskGuard.Core.Throttling;

// Check the real P/Invoke contract as well as the linked production control flow.
var native = typeof(production::DiskGuard.Core.Throttling.ProcessThrottler).Assembly
    .GetType("DiskGuard.Core.Interop.NativeMethods")!;
var method = native.GetMethod("SetIoRateControlInformationJobObject", BindingFlags.Static | BindingFlags.NonPublic)!;
Check(method.ReturnType == typeof(uint), "SetIoRateControlInformationJobObject returns a DWORD error code");

var throttler = new ProcessThrottler();
var handle = new ThrottleHandle { ProcessHandle = new IntPtr(1) };
NativeMethods.RateControlError = 0;
Check(throttler.ApplyLevel2(handle, 30 * 1024 * 1024, 800), "ERROR_SUCCESS enables level 2");
Check(NativeMethods.AssignCalls == 1, "successful rate configuration assigns the process to the job");
Check(handle.IsLevel2 && handle.Level == 2 && handle.CapIops == 800 && throttler.LastError == 0,
    "successful configuration records the active cap");

NativeMethods.RateControlError = 5; // ERROR_ACCESS_DENIED is returned directly; GetLastError is unrelated.
NativeMethods.LastWin32Error = 123;
int assignedBefore = NativeMethods.AssignCalls;
Check(!throttler.ApplyLevel2(handle, 15 * 1024 * 1024, 400), "nonzero native error rejects the new cap");
Check(throttler.LastError == 5 && NativeMethods.AssignCalls == assignedBefore,
    "rate configuration failure reports the returned error without assigning the process");
Check(handle.CapIops == 800, "failed tightening preserves the previous recorded cap");

NativeMethods.RateControlError = 0;
NativeMethods.AssignSucceeds = false;
NativeMethods.LastWin32Error = 87;
var unassigned = new ThrottleHandle { ProcessHandle = new IntPtr(2) };
Check(!throttler.ApplyLevel2(unassigned, 1024, 8), "assignment failure rejects level 2");
Check(throttler.LastError == 87 && !unassigned.IsLevel2 && unassigned.JobHandle == IntPtr.Zero,
    "assignment failure reports GetLastError and releases its job handle");

throttler.DisableLevel2(handle);
Check(!handle.IsLevel2 && handle.CapBytesPerSec == 0 && handle.CapIops == 0 && handle.JobHandle == IntPtr.Zero,
    "disabling clears the cap and closes the job");
CheckInactiveStop();
CheckReleaseDuringSuspend();
CheckStopDuringRun();
CheckFailedSuspend();
CheckRepeatedStartStop();
Console.WriteLine("All throttling checks passed.");

static void CheckInactiveStop()
{
    int resumes = 0;
    NativeMethods.OnResume = _ => { Interlocked.Increment(ref resumes); return 0; };
    new ProcessThrottler().StopLevel3(new ThrottleHandle { ProcessHandle = new IntPtr(41) });
    Check(resumes == 0, "stopping inactive level 3 does not undo another caller's suspension");
}

static void CheckReleaseDuringSuspend()
{
    using var entered = new ManualResetEventSlim();
    using var allowSuspend = new ManualResetEventSlim();
    using var releaseStarted = new ManualResetEventSlim();
    using var closed = new ManualResetEventSlim();
    int suspends = 0, resumes = 0, invalidCalls = 0;
    var handle = new ThrottleHandle { ProcessHandle = new IntPtr(42) };
    NativeMethods.OnSuspend = nativeHandle =>
    {
        entered.Set();
        if (!allowSuspend.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("suspend gate");
        if (closed.IsSet || nativeHandle != new IntPtr(42)) Interlocked.Increment(ref invalidCalls);
        Interlocked.Increment(ref suspends);
        return 0;
    };
    NativeMethods.OnResume = nativeHandle =>
    {
        if (closed.IsSet || nativeHandle != new IntPtr(42)) Interlocked.Increment(ref invalidCalls);
        Interlocked.Increment(ref resumes);
        return 0;
    };
    NativeMethods.OnClose = nativeHandle => { if (nativeHandle == new IntPtr(42)) closed.Set(); };
    var throttler = new ProcessThrottler();
    Check(throttler.StartLevel3(handle, 5000, 5000), "level 3 starts with a live process handle");
    Task? release = null;
    bool closedEarly = false;
    try
    {
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "suspend worker reaches the native call");
        release = Task.Run(() => { releaseStarted.Set(); throttler.Release(handle); });
        Check(releaseStarted.Wait(TimeSpan.FromSeconds(5)), "release begins while suspension is in flight");
        closedEarly = closed.Wait(TimeSpan.FromMilliseconds(100));
    }
    finally
    {
        allowSuspend.Set();
        if (release != null) release.GetAwaiter().GetResult();
        else throttler.Release(handle);
    }
    Check(!closedEarly, "release waits for the in-flight suspension before closing its handle");
    Check(suspends == 1 && resumes == 1 && invalidCalls == 0,
        "release balances its suspension once without using a closed or zero handle");
    Check(closed.IsSet && !handle.HasHandle && handle.SuspendTask == null && handle.SuspendCts == null,
        "release finishes the worker and clears its lifetime resources");
    NativeMethods.OnClose = null;
}

static void CheckFailedSuspend()
{
    using var attempted = new ManualResetEventSlim();
    int resumes = 0;
    NativeMethods.OnSuspend = _ => { attempted.Set(); return -1; };
    NativeMethods.OnResume = _ => { Interlocked.Increment(ref resumes); return 0; };
    var throttler = new ProcessThrottler();
    var handle = new ThrottleHandle { ProcessHandle = new IntPtr(43) };
    throttler.StartLevel3(handle, 50, 20);
    Check(attempted.Wait(TimeSpan.FromSeconds(5)), "failed native suspension is observed");
    throttler.StopLevel3(handle);
    Check(resumes == 0, "failed suspension does not resume a process we never suspended");
}

static void CheckStopDuringRun()
{
    using var resumed = new ManualResetEventSlim();
    int suspends = 0, resumes = 0;
    NativeMethods.OnSuspend = _ => { Interlocked.Increment(ref suspends); return 0; };
    NativeMethods.OnResume = _ => { Interlocked.Increment(ref resumes); resumed.Set(); return 0; };
    var throttler = new ProcessThrottler();
    var handle = new ThrottleHandle { ProcessHandle = new IntPtr(45) };
    throttler.StartLevel3(handle, 5000, 20);
    Check(resumed.Wait(TimeSpan.FromSeconds(5)), "worker reaches the running interval after resuming");
    throttler.StopLevel3(handle);
    Check(suspends == 1 && resumes == 1, "stopping in the running interval does not issue an extra resume");
}

static void CheckRepeatedStartStop()
{
    int suspends = 0, resumes = 0;
    NativeMethods.OnSuspend = _ => { Interlocked.Increment(ref suspends); return 0; };
    NativeMethods.OnResume = _ => { Interlocked.Increment(ref resumes); return 0; };
    var throttler = new ProcessThrottler();
    var handle = new ThrottleHandle { ProcessHandle = new IntPtr(44) };
    for (int i = 0; i < 25; i++)
    {
        throttler.StartLevel3(handle, 50, 20);
        throttler.StopLevel3(handle);
        if (handle.IsLevel3 || handle.SuspendTask != null || handle.SuspendCts != null)
            throw new InvalidOperationException("start/stop cycle " + i + " left its worker running");
    }
    Check(suspends == resumes, "25 rapid start/stop cycles finish their workers and balance every suspension");
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
