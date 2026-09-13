using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskGuard.Core.Localization;

namespace DiskGuard.App.Localization;

/// <summary>按界面语言设置字体与排版方向（阿拉伯语从右往左）。</summary>
public static class UiLanguage
{
    public static void Apply(Control element, AppLanguage language)
    {
        element.FontFamily = new FontFamily(FontStack(language));
        element.FlowDirection = Loc.IsRtl(language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    /// <summary>按语言挑字体栈：优先该语言的原生 UI 字体，再回退到中文字体与 Segoe UI。</summary>
    public static string FontStack(AppLanguage language) => language switch
    {
        AppLanguage.ZhHans => "Microsoft YaHei UI, Microsoft YaHei, Segoe UI",
        AppLanguage.ZhHant => "Microsoft JhengHei UI, Microsoft JhengHei, Microsoft YaHei UI, Segoe UI",
        AppLanguage.Ja => "Yu Gothic UI, Meiryo UI, Microsoft YaHei UI, Segoe UI",
        AppLanguage.Ko => "Malgun Gothic, Microsoft YaHei UI, Segoe UI",
        AppLanguage.Ar => "Segoe UI, Tahoma, Microsoft YaHei UI",
        _ => "Segoe UI, Microsoft YaHei UI"
    };
}
