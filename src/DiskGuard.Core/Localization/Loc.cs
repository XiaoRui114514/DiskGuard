using System.Globalization;

namespace DiskGuard.Core.Localization;

/// <summary>
/// 全局界面语言。所有面向用户的文本都从这里取词条，切换语言后立刻生效，
/// 不需要重启程序（界面绑定监听 <see cref="LanguageChanged"/> 自动刷新）。
/// </summary>
public static class Loc
{
    private static AppLanguage _current = AppLanguage.ZhHans;

    /// <summary>语言切换后触发（界面线程）。</summary>
    public static event Action? LanguageChanged;

    public static AppLanguage Current => _current;

    /// <summary>当前语言是否从右往左排版（阿拉伯语）。</summary>
    public static bool IsRightToLeft => IsRtl(_current);

    /// <summary>设置项里的全部可选语言（枚举值、语言代码、母语名称）。</summary>
    public static IReadOnlyList<(AppLanguage Language, string Code, string NativeName)> Options { get; } = new[]
    {
        (AppLanguage.ZhHans, "zh-Hans", "简体中文"),
        (AppLanguage.ZhHant, "zh-Hant", "繁體中文"),
        (AppLanguage.En, "en", "English"),
        (AppLanguage.Ja, "ja", "日本語"),
        (AppLanguage.Ko, "ko", "한국어"),
        (AppLanguage.Ar, "ar", "العربية")
    };

    public static bool IsRtl(AppLanguage language) => language == AppLanguage.Ar;

    public static string Code(AppLanguage language)
    {
        foreach (var option in Options)
        {
            if (option.Language == language) return option.Code;
        }

        return "en";
    }

    public static string NativeName(AppLanguage language)
    {
        foreach (var option in Options)
        {
            if (option.Language == language) return option.NativeName;
        }

        return "English";
    }

    /// <summary>把设置文件里的语言代码（可为空）解析成具体语言；空值表示跟随系统。</summary>
    public static AppLanguage FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return ResolveSystemLanguage();

        string value = code.Trim().Replace('_', '-');
        foreach (var option in Options)
        {
            if (string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase)) return option.Language;
        }

        // 容忍 zh-CN / zh-TW / en-US 这类完整区域名
        string lower = value.ToLowerInvariant();
        if (lower.StartsWith("zh"))
        {
            return lower.Contains("hant") || lower.Contains("-tw") || lower.Contains("-hk") || lower.Contains("-mo")
                ? AppLanguage.ZhHant
                : AppLanguage.ZhHans;
        }

        if (lower.StartsWith("ja")) return AppLanguage.Ja;
        if (lower.StartsWith("ko")) return AppLanguage.Ko;
        if (lower.StartsWith("ar")) return AppLanguage.Ar;
        if (lower.StartsWith("en")) return AppLanguage.En;

        // 其余系统语言统一回退到英语（国际化默认语言）
        return AppLanguage.En;
    }

    /// <summary>按系统显示语言挑一个默认值（首次运行时用）。</summary>
    public static AppLanguage ResolveSystemLanguage()
    {
        try
        {
            string name = CultureInfo.CurrentUICulture.Name;
            // 读不到系统语言（未设置显示语言的系统 / 不变文化）时按英语兜底：
            // 空串不能再回到 FromCode（那会递归回本方法），也不该让“检测不到”变成简中以外的意外结果。
            if (string.IsNullOrWhiteSpace(name)) return AppLanguage.En;
            return FromCode(name);
        }
        catch
        {
            return AppLanguage.En;
        }
    }

    public static void SetLanguage(AppLanguage language)
    {
        if (_current == language) return;
        _current = language;
        LanguageChanged?.Invoke();
    }

    /// <summary>取当前语言的词条。</summary>
    public static string T(LK key) => Catalog.Text(_current, key);

    /// <summary>
    /// “数字 + 拉丁单位”片段（如 6.6 MB/s）：阿拉伯语是从右往左排版，bidi 会把空格两侧
    /// 拆成两段显示成“MB/s 6.6”，所以这里用 LRM（U+200E）把整段钉成从左到右。其它语言原样返回。
    /// </summary>
    public static string Value(string text) =>
        IsRightToLeft && text.Length > 0 ? "\u200E" + text + "\u200E" : text;

    /// <summary>取当前语言并填充占位符（{0}、{1:0.#} …）。</summary>
    public static string F(LK key, params object?[] args)
    {
        string text = Catalog.Text(_current, key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            // 词条占位符写错时不要崩，直接把原文显示出来
            return text;
        }
    }

    /// <summary>界面绑定用：按词条键名取词条（键名不存在时返回 [!键名]，方便一眼看出漏翻）。</summary>
    public static string ByName(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        return Enum.TryParse<LK>(key, ignoreCase: false, out var parsed)
            ? Catalog.Text(_current, parsed)
            : "[!" + key + "]";
    }

    /// <summary>某个语言缺少哪些词条（供校验脚本使用）。</summary>
    public static IReadOnlyList<string> MissingKeys(AppLanguage language) => Catalog.Missing(language);
}
