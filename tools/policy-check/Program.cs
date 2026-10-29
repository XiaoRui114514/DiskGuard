using DiskGuard.Core.Config;
var settings = new AppSettings { TriggerPercent = 45, RateCapMBps = 12, Language = "ja", AutoStart = false };
var a = DiskPolicy.Capture(settings);
DiskPolicy.Capture(new AppSettings()).Apply(settings);
if (settings.TriggerPercent != 80 || settings.Language != "ja" || settings.AutoStart) throw new Exception("Policy overwrote global preferences");
a.Apply(settings);
if (settings.TriggerPercent != 45 || settings.RateCapMBps != 12) throw new Exception("Policy did not restore settings");
settings.SelectedDiskIdentity = "serial-a";
settings.DiskProfiles["serial-a"] = a;
var path = Path.Combine(Path.GetTempPath(), "DiskGuard-policy-"+Guid.NewGuid()+".json");
try {
 settings.Save(path);
 var loaded=AppSettings.Load(path);
 if(loaded.SelectedDiskIdentity!="serial-a" || loaded.DiskProfiles["serial-a"].RateCapMBps!=12) throw new Exception("Profile persistence failed");
} finally { File.Delete(path); }
Console.WriteLine("Per-disk policy isolation, restore and persistence passed");
