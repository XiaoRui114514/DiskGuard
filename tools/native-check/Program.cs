using System.Diagnostics;
using DiskGuard.Core.Throttling;
if (args.Contains("--child")) { Thread.Sleep(30000); return; }
if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: real Windows process APIs require Windows"); return; }
using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--child" }, UseShellExecute = false, CreateNoWindow = true })!;
var throttler = new ProcessThrottler();
ThrottleHandle? handle = null;
try {
 var priority = child.PriorityClass;
 handle = throttler.ApplyLevel1(child.Id, "DiskGuard-test-child", false, 0, true) ?? throw new Exception(throttler.LastErrorText);
 child.Refresh();
 if(child.PriorityClass != ProcessPriorityClass.BelowNormal) throw new Exception("Native priority not applied");
 if(!throttler.ApplyLevel2(handle, 1024*1024, 10)) Console.WriteLine("INFO: native Job IO cap unavailable on this host, error="+throttler.LastError);
 if(!throttler.StartLevel3(handle,50,20)) throw new Exception("Suspend worker failed to start");
 Thread.Sleep(120);
 throttler.StopLevel3(handle);
 throttler.Release(handle); handle = null;
 child.Refresh();
 if(child.HasExited || child.PriorityClass != priority) throw new Exception("Native process was not restored");
 Console.WriteLine("Native temporary child: priority, suspend/stop and restoration passed");
} finally {
 if(handle!=null) throttler.Release(handle);
 if(!child.HasExited) child.Kill(entireProcessTree:true);
 child.WaitForExit(5000);
}
