using DiskGuard.Core.Localization;

namespace DiskGuard.App.Services;

public enum SystemServiceGroup { Disk, Background, Devices, Gaming, Observe }

public sealed record SystemServiceDefinition(string Name, LK LabelKey, LK AdviceKey, SystemServiceGroup Group)
{
    public bool CanPause => Group != SystemServiceGroup.Observe;
    public string DisplayName => Loc.T(LabelKey);
    public string HelpText => Loc.T(AdviceKey);
}

/// <summary>只允许操作明确列出的可选服务；系统依赖、安全与资源诊断项只读。</summary>
public static class SystemServiceCatalog
{
    public static readonly IReadOnlyList<SystemServiceDefinition> Services = Array.AsReadOnly(new[]
    {
        new SystemServiceDefinition("SysMain", LK.LabelSysMain, LK.SystemAdviceSysMain, SystemServiceGroup.Disk),
        new("WSearch", LK.LabelSearchIndex, LK.SystemAdviceSearch, SystemServiceGroup.Disk),
        new("DoSvc", LK.LabelDeliveryOptimization, LK.SystemAdviceDelivery, SystemServiceGroup.Disk),
        new("wuauserv", LK.LabelWindowsUpdate, LK.SystemAdviceUpdate, SystemServiceGroup.Disk),
        new("BITS", LK.LabelBits, LK.SystemAdviceUpdate, SystemServiceGroup.Disk),
        new("DiagTrack", LK.ServiceTelemetry, LK.AdviceTelemetry, SystemServiceGroup.Background),
        new("PcaSvc", LK.ServiceCompatibility, LK.AdviceCompatibility, SystemServiceGroup.Background),
        new("MapsBroker", LK.ServiceMaps, LK.AdviceMaps, SystemServiceGroup.Background),
        new("TrkWks", LK.ServiceLinkTracking, LK.AdviceLinkTracking, SystemServiceGroup.Background),
        new("Spooler", LK.ServicePrinting, LK.AdvicePrinting, SystemServiceGroup.Devices),
        new("Fax", LK.ServiceFax, LK.AdviceFax, SystemServiceGroup.Devices),
        new("WMPNetworkSvc", LK.ServiceMediaSharing, LK.AdviceMediaSharing, SystemServiceGroup.Devices),
        new("CDPSvc", LK.ServiceConnectedDevices, LK.AdviceConnectedDevices, SystemServiceGroup.Devices),
        new("PhoneSvc", LK.ServicePhone, LK.AdvicePhone, SystemServiceGroup.Devices),
        new("XblAuthManager", LK.ServiceXboxAuth, LK.AdviceXboxAuth, SystemServiceGroup.Gaming),
        new("XblGameSave", LK.ServiceXboxSave, LK.AdviceXboxSave, SystemServiceGroup.Gaming),
        new("XboxNetApiSvc", LK.ServiceXboxNetwork, LK.AdviceXboxNetwork, SystemServiceGroup.Gaming),
        new("WinDefend", LK.CardDefender, LK.SystemAdviceDefender, SystemServiceGroup.Observe),
        new("wscsvc", LK.SystemSecurityCenter, LK.SystemAdviceSecurity, SystemServiceGroup.Observe),
        new("SecurityHealthService", LK.SystemSecurityHealth, LK.SystemAdviceSecurity, SystemServiceGroup.Observe),
        new("WerSvc", LK.ServiceErrorReporting, LK.AdviceErrorReporting, SystemServiceGroup.Observe),
        new("WpnService", LK.ServiceNotifications, LK.AdviceNotifications, SystemServiceGroup.Observe),
        new("DusmSvc", LK.ServiceDataUsage, LK.AdviceDataUsage, SystemServiceGroup.Observe),
        new("FontCache", LK.ServiceFontCache, LK.AdviceFontCache, SystemServiceGroup.Observe)
    });

    public static SystemServiceDefinition? Find(string name) => Services.FirstOrDefault(service =>
        service.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
