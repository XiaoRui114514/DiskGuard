using System.Text.Json;

namespace DiskGuard.Core.Util;

public static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskGuard");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string ActiveThrottleFile => Path.Combine(DataDirectory, "active-throttles.json");
}

public sealed class ActiveThrottleRecord
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public int AppliedIoPriority { get; set; } = -1;
    public int OriginalIoPriority { get; set; } = -1;
    public uint AppliedPriorityClass { get; set; }
    public uint OriginalPriorityClass { get; set; }
    public DateTime StartedAtUtc { get; set; }
}

public static class ActiveThrottleStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(IEnumerable<ActiveThrottleRecord> records)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var list = records.ToList();
            if (list.Count == 0)
            {
                if (File.Exists(AppPaths.ActiveThrottleFile)) File.Delete(AppPaths.ActiveThrottleFile);
                return;
            }

            File.WriteAllText(AppPaths.ActiveThrottleFile, JsonSerializer.Serialize(list, Options));
        }
        catch
        {
            // 状态文件仅用于崩溃兜底，写入失败可忽略
        }
    }

    public static List<ActiveThrottleRecord> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ActiveThrottleFile)) return new List<ActiveThrottleRecord>();
            return JsonSerializer.Deserialize<List<ActiveThrottleRecord>>(File.ReadAllText(AppPaths.ActiveThrottleFile))
                   ?? new List<ActiveThrottleRecord>();
        }
        catch
        {
            return new List<ActiveThrottleRecord>();
        }
    }
}
