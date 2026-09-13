using System.ComponentModel;
using DiskGuard.Core.Config;
using DiskGuard.Core.Localization;

namespace DiskGuard.App.ViewModels;

/// <summary>设置页语言下拉项（用母语名称显示，语言看不懂时也能找到自己的语言）。</summary>
public sealed class LanguageOption
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>设置界面的可绑定副本，保存时写回 AppSettings。</summary>
public sealed class SettingsVm : INotifyPropertyChanged
{
    private double _triggerPercent;
    private double _triggerSeconds;
    private double _recoverPercent;
    private double _recoverSeconds;
    private double _triggerOccupancyPercent;
    private double _targetOccupancyPercent;
    private double _minIops;
    private bool _enableEmergencyThrottle;
    private double _emergencyPercent;
    private bool _lowerIoPriority;
    private int _ioPriorityLevel;
    private bool _lowerCpuPriority;
    private bool _enableRateCap;
    private double _rateCapMBps;
    private double _escalateSeconds;
    private bool _enableSuspendMode;
    private int _suspendRunMs;
    private int _suspendPauseMs;
    private bool _protectForeground;
    private string _whitelistText = string.Empty;
    private bool _startMinimized;
    private bool _autoStart;
    private bool _writeLogFile;
    private bool _showBalloon;
    private string _language = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public double TriggerPercent { get => _triggerPercent; set => Set(ref _triggerPercent, value); }
    public double TriggerSeconds { get => _triggerSeconds; set => Set(ref _triggerSeconds, value); }
    public double RecoverPercent { get => _recoverPercent; set => Set(ref _recoverPercent, value); }
    public double RecoverSeconds { get => _recoverSeconds; set => Set(ref _recoverSeconds, value); }
    public double TriggerOccupancyPercent { get => _triggerOccupancyPercent; set => Set(ref _triggerOccupancyPercent, value); }
    public double TargetOccupancyPercent { get => _targetOccupancyPercent; set => Set(ref _targetOccupancyPercent, value); }
    public double MinIops { get => _minIops; set => Set(ref _minIops, value); }
    public bool EnableEmergencyThrottle { get => _enableEmergencyThrottle; set => Set(ref _enableEmergencyThrottle, value); }
    public double EmergencyPercent { get => _emergencyPercent; set => Set(ref _emergencyPercent, value); }
    public bool LowerIoPriority { get => _lowerIoPriority; set => Set(ref _lowerIoPriority, value); }
    public int IoPriorityLevel { get => _ioPriorityLevel; set => Set(ref _ioPriorityLevel, value); }
    public bool LowerCpuPriority { get => _lowerCpuPriority; set => Set(ref _lowerCpuPriority, value); }
    public bool EnableRateCap { get => _enableRateCap; set => Set(ref _enableRateCap, value); }
    public double RateCapMBps { get => _rateCapMBps; set => Set(ref _rateCapMBps, value); }
    public double EscalateSeconds { get => _escalateSeconds; set => Set(ref _escalateSeconds, value); }
    public bool EnableSuspendMode { get => _enableSuspendMode; set => Set(ref _enableSuspendMode, value); }
    public int SuspendRunMs { get => _suspendRunMs; set => Set(ref _suspendRunMs, value); }
    public int SuspendPauseMs { get => _suspendPauseMs; set => Set(ref _suspendPauseMs, value); }
    public bool ProtectForeground { get => _protectForeground; set => Set(ref _protectForeground, value); }
    public string WhitelistText { get => _whitelistText; set => Set(ref _whitelistText, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    public bool AutoStart { get => _autoStart; set => Set(ref _autoStart, value); }
    public bool WriteLogFile { get => _writeLogFile; set => Set(ref _writeLogFile, value); }
    public bool ShowBalloon { get => _showBalloon; set => Set(ref _showBalloon, value); }
    public string Language { get => _language; set => Set(ref _language, value); }

    /// <summary>可选语言列表（与 <see cref="Loc.Options"/> 一一对应）。</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } = Loc.Options
        .Select(option => new LanguageOption { Code = option.Code, DisplayName = option.NativeName })
        .ToList();

    public static SettingsVm From(AppSettings settings) => new()
    {
        TriggerPercent = settings.TriggerPercent,
        TriggerSeconds = settings.TriggerSeconds,
        RecoverPercent = settings.RecoverPercent,
        RecoverSeconds = settings.RecoverSeconds,
        TriggerOccupancyPercent = settings.TriggerOccupancyPercent,
        TargetOccupancyPercent = settings.TargetOccupancyPercent,
        MinIops = settings.MinIops,
        EnableEmergencyThrottle = settings.EnableEmergencyThrottle,
        EmergencyPercent = settings.EmergencyPercent,
        LowerIoPriority = settings.LowerIoPriority,
        IoPriorityLevel = settings.IoPriorityLevel,
        LowerCpuPriority = settings.LowerCpuPriority,
        EnableRateCap = settings.EnableRateCap,
        RateCapMBps = settings.RateCapMBps,
        EscalateSeconds = settings.EscalateSeconds,
        EnableSuspendMode = settings.EnableSuspendMode,
        SuspendRunMs = settings.SuspendRunMs,
        SuspendPauseMs = settings.SuspendPauseMs,
        ProtectForeground = settings.ProtectForeground,
        WhitelistText = string.Join(Environment.NewLine, settings.Whitelist),
        StartMinimized = settings.StartMinimized,
        AutoStart = AutoStartChecked(settings),
        WriteLogFile = settings.WriteLogFile,
        ShowBalloon = settings.ShowBalloon,
        Language = Loc.Code(Loc.FromCode(settings.Language))
    };

    private static bool AutoStartChecked(AppSettings settings) => settings.AutoStart;

    public void ApplyTo(AppSettings settings)
    {
        settings.TriggerPercent = Math.Clamp(TriggerPercent, 1, 100);
        settings.TriggerSeconds = Math.Clamp(TriggerSeconds, 0, 600);
        settings.RecoverPercent = Math.Clamp(RecoverPercent, 0, 99);
        settings.RecoverSeconds = Math.Clamp(RecoverSeconds, 1, 600);
        settings.TriggerOccupancyPercent = Math.Clamp(TriggerOccupancyPercent, 1, 100);
        settings.TargetOccupancyPercent = Math.Clamp(TargetOccupancyPercent, 1, 99);
        settings.MinIops = Math.Clamp(MinIops, 0, 100000);
        settings.EnableEmergencyThrottle = EnableEmergencyThrottle;
        settings.EmergencyPercent = Math.Clamp(EmergencyPercent, 50, 100);
        settings.LowerIoPriority = LowerIoPriority;
        settings.IoPriorityLevel = Math.Clamp(IoPriorityLevel, 0, 1);
        settings.LowerCpuPriority = LowerCpuPriority;
        settings.EnableRateCap = EnableRateCap;
        settings.RateCapMBps = Math.Clamp(RateCapMBps, 1, 10000);
        settings.EscalateSeconds = Math.Clamp(EscalateSeconds, 1, 600);
        settings.EnableSuspendMode = EnableSuspendMode;
        settings.SuspendRunMs = Math.Clamp(SuspendRunMs, 50, 5000);
        settings.SuspendPauseMs = Math.Clamp(SuspendPauseMs, 20, 5000);
        settings.ProtectForeground = ProtectForeground;
        settings.StartMinimized = StartMinimized;
        settings.AutoStart = AutoStart;
        settings.WriteLogFile = WriteLogFile;
        settings.ShowBalloon = ShowBalloon;
        settings.Language = Language;

        settings.Whitelist = WhitelistText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void ReloadFrom(AppSettings settings)
    {
        var copy = From(settings);
        TriggerPercent = copy.TriggerPercent;
        TriggerSeconds = copy.TriggerSeconds;
        RecoverPercent = copy.RecoverPercent;
        RecoverSeconds = copy.RecoverSeconds;
        TriggerOccupancyPercent = copy.TriggerOccupancyPercent;
        TargetOccupancyPercent = copy.TargetOccupancyPercent;
        MinIops = copy.MinIops;
        EnableEmergencyThrottle = copy.EnableEmergencyThrottle;
        EmergencyPercent = copy.EmergencyPercent;
        LowerIoPriority = copy.LowerIoPriority;
        IoPriorityLevel = copy.IoPriorityLevel;
        LowerCpuPriority = copy.LowerCpuPriority;
        EnableRateCap = copy.EnableRateCap;
        RateCapMBps = copy.RateCapMBps;
        EscalateSeconds = copy.EscalateSeconds;
        EnableSuspendMode = copy.EnableSuspendMode;
        SuspendRunMs = copy.SuspendRunMs;
        SuspendPauseMs = copy.SuspendPauseMs;
        ProtectForeground = copy.ProtectForeground;
        WhitelistText = copy.WhitelistText;
        StartMinimized = copy.StartMinimized;
        AutoStart = copy.AutoStart;
        WriteLogFile = copy.WriteLogFile;
        ShowBalloon = copy.ShowBalloon;
        Language = copy.Language;
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
