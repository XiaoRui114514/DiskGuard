using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskGuard.Core.Config;

public sealed class AppSettings
{
    [JsonPropertyName("diskNumber")] public int DiskNumber { get; set; } = 0;

    [JsonPropertyName("triggerPercent")] public double TriggerPercent { get; set; } = 80;
    [JsonPropertyName("triggerSeconds")] public double TriggerSeconds { get; set; } = 5;
    [JsonPropertyName("recoverPercent")] public double RecoverPercent { get; set; } = 60;
    [JsonPropertyName("recoverSeconds")] public double RecoverSeconds { get; set; } = 10;

    /// <summary>触发限速的"磁盘占用百分比"（该进程让磁盘忙碌的时间占比达到该值才限速）。</summary>
    [JsonPropertyName("triggerOccupancyPercent")] public double TriggerOccupancyPercent { get; set; } = 30;

    /// <summary>限速目标：把该进程的磁盘占用百分比压到该值以下。</summary>
    [JsonPropertyName("targetOccupancyPercent")] public double TargetOccupancyPercent { get; set; } = 20;

    /// <summary>最小磁盘 IO 次数（次/秒），低于该值视为噪音不动手。</summary>
    [JsonPropertyName("minIops")] public double MinIops { get; set; } = 20;

    /// <summary>紧急保护：磁盘忙率达到该值时立即限速（不等观察窗口、不校验占比），用于防止突然卡死。</summary>
    [JsonPropertyName("enableEmergencyThrottle")] public bool EnableEmergencyThrottle { get; set; } = true;

    [JsonPropertyName("emergencyPercent")] public double EmergencyPercent { get; set; } = 98;

    /// <summary>兼容旧配置：附加的最低速率门槛（0 表示不启用）。</summary>
    [JsonPropertyName("minRateMBps")] public double MinRateMBps { get; set; }

    [JsonPropertyName("lowerIoPriority")] public bool LowerIoPriority { get; set; } = true;
    [JsonPropertyName("ioPriorityLevel")] public int IoPriorityLevel { get; set; } = 0;   // 0=极低 1=低
    [JsonPropertyName("lowerCpuPriority")] public bool LowerCpuPriority { get; set; } = true;

    [JsonPropertyName("enableRateCap")] public bool EnableRateCap { get; set; } = true;
    [JsonPropertyName("rateCapMBps")] public double RateCapMBps { get; set; } = 30;
    [JsonPropertyName("escalateSeconds")] public double EscalateSeconds { get; set; } = 5;

    [JsonPropertyName("enableSuspendMode")] public bool EnableSuspendMode { get; set; }
    [JsonPropertyName("suspendRunMs")] public int SuspendRunMs { get; set; } = 300;
    [JsonPropertyName("suspendPauseMs")] public int SuspendPauseMs { get; set; } = 200;

    [JsonPropertyName("protectForeground")] public bool ProtectForeground { get; set; } = true;
    [JsonPropertyName("whitelist")] public List<string> Whitelist { get; set; } = new() { "TextInputHost.exe", "ctfmon.exe" };

    [JsonPropertyName("startMinimized")] public bool StartMinimized { get; set; } = true;
    [JsonPropertyName("autoStart")] public bool AutoStart { get; set; } = true;
    [JsonPropertyName("writeLogFile")] public bool WriteLogFile { get; set; } = true;
    [JsonPropertyName("showBalloon")] public bool ShowBalloon { get; set; } = true;

    /// <summary>界面语言代码（zh-Hans / zh-Hant / en / ja / ko / ar）；留空表示跟随系统。</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (loaded != null)
                {
                    loaded.Whitelist ??= new List<string>();
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }

        return new AppSettings();
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 先写临时文件再原子替换：万一写到一半掉电/被杀，配置不会变成半截 JSON
        // （半截 JSON 会被 Load 当成损坏文件，用户设置就静默丢了）
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, Options));
        File.Move(tempPath, path, overwrite: true);
    }

    public bool IsWhitelisted(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string name = processName.Trim();
        string withExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        string withoutExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        foreach (var item in Whitelist)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            string entry = item.Trim();
            if (entry.Equals(withExe, StringComparison.OrdinalIgnoreCase) ||
                entry.Equals(withoutExe, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
