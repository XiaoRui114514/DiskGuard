using DiskGuard.App.Services;
using DiskGuard.Core.Localization;
using DiskGuard.Core.Util;

namespace DiskGuard.App.ViewModels;

public sealed class SystemStartupRowVm
{
    public SystemStartupItem Item { get; }

    public SystemStartupRowVm(SystemStartupItem item) => Item = item;

    public string Name => SystemServiceCatalog.Find(Item.Name)?.DisplayName ?? Item.Name;

    public string MemoryText => Item.ProcessId > 0 && Item.MemoryBytes is >= 0
        ? Loc.Value($"{Item.MemoryBytes.Value / 1048576d:0.#} MB") +
          (Item.HostServiceCount > 1 ? " · " + Loc.T(LK.SystemHostShared) : string.Empty)
        : "—";

    public string IoText => Item.ProcessId > 0 && Item.IoBytesPerSecond is { } rate && double.IsFinite(rate) && rate >= 0
        ? Loc.Value(ProcessUtil.FormatRate(rate)) : "—";

    public string ResourceDetails => Item.ProcessId > 0
        ? Loc.F(LK.SystemHostDetailsFormat, Item.ProcessId, Item.HostServiceCount) : Item.Name;

    public string StateText => Loc.T(!Item.Exists ? LK.SystemServiceMissing : Item.State switch
    {
        "Running" => LK.SystemServiceRunning,
        "Stopped" => LK.SystemServiceStopped,
        "Paused" => LK.SystemServicePaused,
        "Start Pending" or "Stop Pending" or "Continue Pending" or "Pause Pending" => LK.SystemServicePending,
        _ => LK.SystemServiceUnknown
    });

    public string StartModeText => Loc.T(!Item.Exists ? LK.SystemServiceUnknown : Item.StartMode switch
    {
        "Auto" when Item.DelayedAutoStart => LK.SystemStartDelayed,
        "Auto" => LK.SystemStartAuto,
        "Manual" => LK.SystemStartManual,
        "Disabled" => LK.SystemStartDisabled,
        "Boot" or "System" => LK.SystemStartBoot,
        _ => LK.SystemServiceUnknown
    });

    public string AdviceText => Loc.T(AdviceKey);

    internal LK AdviceKey
    {
        get
        {
            if (!Item.Exists) return LK.SystemAdviceMissing;
            var definition = SystemServiceCatalog.Find(Item.Name);
            if (definition?.CanPause == true && Item.State == "Stopped" && definition.AdviceKey != LK.SystemAdviceUpdate)
                return LK.SystemAdviceStopped;
            return definition?.AdviceKey ?? LK.SystemServiceUnknown;
        }
    }
}
