namespace DiskGuard.Core.Config;

/// <summary>Only throttling policy is per disk; language/startup/services remain global.</summary>
public sealed class DiskPolicy
{
    public double TriggerPercent { get; set; }
    public double TriggerSeconds { get; set; }
    public double RecoverPercent { get; set; }
    public double RecoverSeconds { get; set; }
    public double TriggerOccupancyPercent { get; set; }
    public double TargetOccupancyPercent { get; set; }
    public double MinIops { get; set; }
    public bool EnableEmergencyThrottle { get; set; }
    public double EmergencyPercent { get; set; }
    public double MinRateMBps { get; set; }
    public bool LowerIoPriority { get; set; }
    public int IoPriorityLevel { get; set; }
    public bool LowerCpuPriority { get; set; }
    public bool EnableRateCap { get; set; }
    public double RateCapMBps { get; set; }
    public double EscalateSeconds { get; set; }
    public bool EnableSuspendMode { get; set; }
    public int SuspendRunMs { get; set; }
    public int SuspendPauseMs { get; set; }
    public bool ProtectForeground { get; set; }
    public static DiskPolicy Capture(AppSettings s) => new()
    {
        TriggerPercent = s.TriggerPercent,
        TriggerSeconds = s.TriggerSeconds,
        RecoverPercent = s.RecoverPercent,
        RecoverSeconds = s.RecoverSeconds,
        TriggerOccupancyPercent = s.TriggerOccupancyPercent,
        TargetOccupancyPercent = s.TargetOccupancyPercent,
        MinIops = s.MinIops,
        EnableEmergencyThrottle = s.EnableEmergencyThrottle,
        EmergencyPercent = s.EmergencyPercent,
        MinRateMBps = s.MinRateMBps,
        LowerIoPriority = s.LowerIoPriority,
        IoPriorityLevel = s.IoPriorityLevel,
        LowerCpuPriority = s.LowerCpuPriority,
        EnableRateCap = s.EnableRateCap,
        RateCapMBps = s.RateCapMBps,
        EscalateSeconds = s.EscalateSeconds,
        EnableSuspendMode = s.EnableSuspendMode,
        SuspendRunMs = s.SuspendRunMs,
        SuspendPauseMs = s.SuspendPauseMs,
        ProtectForeground = s.ProtectForeground,
    };
    public void Apply(AppSettings s)
    {
        s.TriggerPercent = TriggerPercent;
        s.TriggerSeconds = TriggerSeconds;
        s.RecoverPercent = RecoverPercent;
        s.RecoverSeconds = RecoverSeconds;
        s.TriggerOccupancyPercent = TriggerOccupancyPercent;
        s.TargetOccupancyPercent = TargetOccupancyPercent;
        s.MinIops = MinIops;
        s.EnableEmergencyThrottle = EnableEmergencyThrottle;
        s.EmergencyPercent = EmergencyPercent;
        s.MinRateMBps = MinRateMBps;
        s.LowerIoPriority = LowerIoPriority;
        s.IoPriorityLevel = IoPriorityLevel;
        s.LowerCpuPriority = LowerCpuPriority;
        s.EnableRateCap = EnableRateCap;
        s.RateCapMBps = RateCapMBps;
        s.EscalateSeconds = EscalateSeconds;
        s.EnableSuspendMode = EnableSuspendMode;
        s.SuspendRunMs = SuspendRunMs;
        s.SuspendPauseMs = SuspendPauseMs;
        s.ProtectForeground = ProtectForeground;
    }
}
