using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskGuard.App.Localization;
using DiskGuard.App.ViewModels;
using DiskGuard.Core.Localization;

namespace DiskGuard.App;

/// <summary>
/// 首次运行的语言选择框：默认按系统语言预选，切换时界面立刻按所选语言预览；
/// 点「确定」记住选择，点「稍后再说」/直接关闭则保持“跟随系统语言”。
/// </summary>
public partial class FirstRunWindow : Window, INotifyPropertyChanged
{
    private readonly AppLanguage _detected;
    private string _selectedCode;

    /// <summary>用户确认的语言代码；未确认时为 null（表示保持跟随系统语言）。</summary>
    public string? ChosenCode { get; private set; }

    public IReadOnlyList<LanguageOption> Languages { get; } = Loc.Options
        .Select(option => new LanguageOption { Code = option.Code, DisplayName = option.NativeName })
        .ToList();

    public string SelectedCode
    {
        get => _selectedCode;
        set
        {
            if (_selectedCode == value) return;
            _selectedCode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCode)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FirstRunWindow()
    {
        _detected = Loc.Current;                 // App 启动时已按系统语言设好
        _selectedCode = Loc.Code(_detected);

        InitializeComponent();
        DataContext = this;

        UiLanguage.Apply(this, _detected);
        RefreshDetectedText(_detected);
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 从终端 / 计划任务之类没有前台权限的进程启动时，Windows 不会把新窗口提到最前，
    /// 首次启动的语言框就会被别的窗口挡住 —— 这里顶一次，之后仍是普通窗口。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 等窗口真正显示出来再顶，否则 Topmost 切换发生在映射之前，等于没做
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            Activate();
            if (IsActive) return;

            Topmost = true;
            Topmost = false;
            Activate();
        }));
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedValue is not string code) return;

        AppLanguage language = Loc.FromCode(code);
        if (Loc.Current == language) return;

        Loc.SetLanguage(language);               // 立刻预览
        UiLanguage.Apply(this, language);
        RefreshDetectedText(language);
    }

    /// <summary>“已按系统语言预选：xxx”这一行始终显示探测结果，不随预览语言变化。</summary>
    private void RefreshDetectedText(AppLanguage language)
    {
        try
        {
            DetectedText.Text = Loc.F(LK.FirstRunDetectedFormat, Loc.NativeName(_detected));
        }
        catch
        {
            // 控件尚未初始化时忽略
        }
    }

    private void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        ChosenCode = LanguageCombo.SelectedValue as string;
        DialogResult = true;
    }

    private void OnLaterClicked(object sender, RoutedEventArgs e)
    {
        Loc.SetLanguage(_detected);               // 放弃预览，回到跟随系统
        ChosenCode = null;
        DialogResult = false;
    }
}
