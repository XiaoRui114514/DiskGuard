using System.ComponentModel;
using System.Windows.Data;
using DiskGuard.Core.Localization;

namespace DiskGuard.App.Localization;

/// <summary>
/// XAML 文本的绑定源：<c>Text="{Binding [ColProcess], Source={x:Static loc:LocSource.Instance}}"</c>。
/// 语言切换时发一次 <c>Item[]</c> 变更通知，整个界面文本立即刷新（不用重启程序）。
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource()
    {
        Loc.LanguageChanged += () =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Loc.ByName(key);
}
