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
Console.WriteLine("All throttling checks passed.");

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
