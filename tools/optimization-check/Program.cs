using DiskGuard.App.Services;
using DiskGuard.App.ViewModels;
using DiskGuard.Core.Localization;

Loc.SetLanguage(AppLanguage.ZhHans);
var refreshGate = new UiRefreshGate();
var ordinarySnapshot = new DiskGuard.Core.Engine.EngineSnapshot { DiskNumber = 0, StateKind = "monitoring" };
long refreshStart = System.Diagnostics.Stopwatch.GetTimestamp();
Check(refreshGate.ShouldRefresh(ordinarySnapshot, refreshStart), "first UI snapshot is immediate");
Check(!refreshGate.ShouldRefresh(ordinarySnapshot, refreshStart + System.Diagnostics.Stopwatch.Frequency), "ordinary UI updates are coalesced for two seconds");
Check(refreshGate.ShouldRefresh(ordinarySnapshot, refreshStart + 2 * System.Diagnostics.Stopwatch.Frequency), "ordinary readings refresh after two seconds");
Check(refreshGate.ShouldRefresh(new() { DiskNumber = 0, StateKind = "throttled", ActivePid = 42 }, refreshStart + 2 * System.Diagnostics.Stopwatch.Frequency), "throttle and target changes bypass the UI delay");
Check(refreshGate.ShouldRefresh(new() { DiskNumber = 0, StateKind = "throttled", ActivePid = 42, Paused = true }, refreshStart + 2 * System.Diagnostics.Stopwatch.Frequency), "pause changes bypass the UI delay");
refreshGate.Reset();
Check(refreshGate.ShouldRefresh(ordinarySnapshot, refreshStart), "restoring the window bypasses the UI delay");
var snapshot = SystemStartupService.ParseStatus("""
[
  {"Name":"SysMain","State":"Stopped","StartMode":"Manual","DelayedAutoStart":false},
  {"Name":"WSearch","State":"Running","StartMode":"Manual","DelayedAutoStart":true},
  {"Name":"BITS","State":"Running","StartMode":"Auto","DelayedAutoStart":true},
  {"Name":"WinDefend","State":"Running","StartMode":"Auto","DelayedAutoStart":false},
  {"Name":"wscsvc","State":"Stopped","StartMode":"Disabled","DelayedAutoStart":true},
  {"Name":"CDPSvc","State":"Running","StartMode":"Auto","ProcessId":1234,"MemoryBytes":104857600,"HostServiceCount":3,"IoBytesPerSecond":1536},
  {"Name":"PhoneSvc","State":"Running","StartMode":"Manual","ProcessId":1234,"MemoryBytes":104857600,"HostServiceCount":3,"IoBytesPerSecond":1536}
]
""");
Check(snapshot.Available && snapshot.Items.Count == 24, "catalog covers twenty-four system services");
Check(ServiceControlService.PausableServices.Count == 17, "seventeen explicit optional service switches");
var rows = snapshot.Items.ToDictionary(item => item.Name, item => new SystemStartupRowVm(item));
Check(rows["WSearch"].StartModeText == Loc.T(LK.SystemStartManual),
    "a stale delay flag must not turn manual startup into automatic startup");
Check(rows["BITS"].StartModeText == Loc.T(LK.SystemStartDelayed), "delayed automatic startup is distinct");
Check(rows["WinDefend"].StartModeText == Loc.T(LK.SystemStartAuto), "ordinary automatic startup is distinct");
Check(rows["wscsvc"].StartModeText == Loc.T(LK.SystemStartDisabled), "disabled overrides a stale delay flag");
Check(rows["SysMain"].AdviceKey == LK.SystemAdviceStopped, "already-stopped prefetch needs no further pause");
Check(rows["WSearch"].AdviceKey == LK.SystemAdviceSearch, "running indexing explains the search tradeoff");
Check(rows["wscsvc"].AdviceKey == LK.SystemAdviceSecurity, "security status services keep their retain recommendation");
Check(rows["WinDefend"].AdviceKey == LK.SystemAdviceDefender, "Defender recommends low-impact scanning");
Check(!rows["DoSvc"].Item.Exists && rows["DoSvc"].StateText == Loc.T(LK.SystemServiceMissing),
    "an absent service is not reported as stopped");
Check(rows["DoSvc"].AdviceKey == LK.SystemAdviceMissing, "absent optional services need no action");
Check(rows["CDPSvc"].MemoryText.Contains("100 MB") && rows["CDPSvc"].MemoryText.Contains(Loc.T(LK.SystemHostShared)),
    "shared host memory is explicitly labelled, not presented as exclusive service memory");
Check(rows["CDPSvc"].IoText.Contains("KB/s") && rows["CDPSvc"].Item.HostServiceCount == 3,
    "I/O rate and all-host service count survive the snapshot");
Check(rows["PhoneSvc"].MemoryText == rows["CDPSvc"].MemoryText, "shared rows show the same host measurement");
Check(rows["WSearch"].MemoryText == "—" && rows["WSearch"].IoText == "—", "missing counters are not reported as zero");
foreach (string name in new[] { "WinDefend", "wscsvc", "SecurityHealthService", "FontCache", "DusmSvc", "WpnService", "WerSvc", "Dnscache", "DPS", "*", "Spooler & whoami" })
    Check(!ServiceControlService.IsPausable(name), "protected or arbitrary service is not actionable: " + name);
Check(ServiceControlService.IsPausable("spooler") && ServiceControlService.IsPausable("XblGameSave"),
    "optional services support case-insensitive explicit matching");
var probes = ServiceControlService.FromSnapshot(snapshot);
Check(probes["WSearch"].StateCode == 4 && probes["SysMain"].StateCode == 1 && !probes["Fax"].Exists,
    "switches and status table derive from the same service snapshot");
var option = new SystemServiceOptionVm(SystemServiceCatalog.Find("Spooler")!);
int notifications = 0;
option.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(option.IsRunning)) notifications++; };
var runningProbe = new ServiceProbe("Spooler", true, true, 4, "");
option.ApplyState(runningProbe, false);
Check(option.IsRunning && option.IsEnabled, "a running optional service can be operated");
option.ApplyState(runningProbe, true);
Check(option.IsRunning && !option.IsEnabled && notifications == 2, "busy state disables actions and refreshes the switch value");
option.ApplyState(new("Spooler", true, false, 3, ""), false);
Check(!option.IsEnabled, "transitional services cannot be toggled prematurely");
option.ApplyState(new("Spooler", false, false, -1, ""), false);
Check(!option.IsEnabled, "unavailable services cannot be toggled");
var observer = new SystemServiceOptionVm(SystemServiceCatalog.Find("WinDefend")!);
observer.ApplyState(new("WinDefend", true, true, 4, ""), false);
Check(!observer.IsEnabled, "read-only security services have no generic service control");
foreach (string badData in new[] { "", "[]", "null", "Access denied", "{}", "[{\"Name\":\"unrelated\"}]" })
    Check(!SystemStartupService.ParseStatus(badData).Available, "failed or empty reads are not healthy snapshots");

var pending = new SystemStartupRowVm(new("SysMain", true, "Stop Pending", "Manual", false));
Check(pending.StateText == Loc.T(LK.SystemServicePending), "transitional service state is not stopped");
var unknown = new SystemStartupRowVm(new("SysMain", true, "Future state", "Future mode", false));
Check(unknown.StateText == Loc.T(LK.SystemServiceUnknown) && unknown.StartModeText == Loc.T(LK.SystemServiceUnknown),
    "unrecognized Windows states remain unknown");

foreach (AppLanguage language in Enum.GetValues<AppLanguage>())
{
    Loc.SetLanguage(language);
    Check(rows.Values.All(row => !row.AdviceText.Contains("[!") && !row.StartModeText.Contains("[!")),
        "recommendations and startup types exist in " + language);
    Check(SystemServiceCatalog.Services.All(service => !service.DisplayName.Contains("[!") && !service.HelpText.Contains("[!")),
        "every optional feature has a localized description in " + language);
    Check(!Loc.F(LK.SystemStartupSummaryFormat, 2, 3, 24, 10).Contains("{3}"), "four-part status summary formats correctly");
}
Loc.SetLanguage(AppLanguage.ZhHans);

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    var live = SystemStartupService.GetStatus();
    Check(live.Available, "live read-only Windows service query: " + live.Error);
    Check(live.Items.Count == 24 && live.Items.Any(item => item.Exists), "live query returned system services");
    var ownResources = DiskGuard.Core.Monitoring.ProcessResourceSampler.Sample(new[] { Environment.ProcessId, Environment.ProcessId, 0 });
    Check(ownResources.Count == 1 && ownResources[Environment.ProcessId].MemoryBytes is > 0, "readable process memory and PID deduplication");
    Check(ownResources[Environment.ProcessId].IoBytesPerSecond is >= 0, "readable process I/O counters");
    foreach (var item in live.Items)
    {
        var row = new SystemStartupRowVm(item);
        Console.WriteLine($"{row.Name}: {row.StateText}; {row.StartModeText}; PID {item.ProcessId}; {row.MemoryText}; {row.IoText}");
    }
}
Console.WriteLine("All system optimization checks passed; no system settings were changed.");

static void Check(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
    Console.WriteLine("PASS: " + message);
}
