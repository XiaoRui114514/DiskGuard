using System.ComponentModel;
using DiskGuard.App.Services;

namespace DiskGuard.App.ViewModels;

public sealed class SystemServiceOptionVm : INotifyPropertyChanged
{
    public SystemServiceDefinition Definition { get; }
    public string Name => Definition.Name;
    public string DisplayName => Definition.DisplayName;
    public string HelpText => Definition.HelpText;
    public bool IsRunning { get; private set; }
    public bool IsEnabled { get; private set; }

    public SystemServiceOptionVm(SystemServiceDefinition definition) => Definition = definition;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyState(ServiceProbe state, bool busy)
    {
        IsRunning = state.Exists && state.StateCode == 4;
        IsEnabled = Definition.CanPause && state.Exists && state.StateCode is 1 or 4 && !busy;
        // 即使值相同也重新通知：操作失败或权限不足时，必须撤回用户刚点下的开关状态。
        PropertyChanged?.Invoke(this, new(nameof(IsRunning)));
        PropertyChanged?.Invoke(this, new(nameof(IsEnabled)));
    }

    public void RefreshTexts()
    {
        PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new(nameof(HelpText)));
    }
}
