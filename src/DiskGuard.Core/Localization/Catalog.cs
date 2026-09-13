namespace DiskGuard.Core.Localization;

/// <summary>
/// 全部界面词条。每种语言一个文件（Catalog.zh-Hans.cs 等），键统一是 <see cref="LK"/>。
/// 新增词条时：先在 LK 里加键，再补齐 6 个语言文件，最后跑 `DiskGuard.Cli.exe i18n` 校验。
/// </summary>
internal static partial class Catalog
{
    private static readonly Dictionary<AppLanguage, Dictionary<LK, string>> Maps = new()
    {
        [AppLanguage.ZhHans] = ZhHans(),
        [AppLanguage.ZhHant] = ZhHant(),
        [AppLanguage.En] = En(),
        [AppLanguage.Ja] = Ja(),
        [AppLanguage.Ko] = Ko(),
        [AppLanguage.Ar] = Ar()
    };

    internal static string Text(AppLanguage language, LK key)
    {
        var map = Maps.TryGetValue(language, out var found) ? found : Maps[AppLanguage.ZhHans];
        return map.TryGetValue(key, out string? text) ? text : "[!" + key + "]";
    }

    /// <summary>某个语言缺失的词条（正常情况下应为空）。</summary>
    internal static IReadOnlyList<string> Missing(AppLanguage language)
    {
        if (!Maps.TryGetValue(language, out var map)) return new[] { "<未定义的语言>" };

        var missing = new List<string>();
        foreach (LK key in Enum.GetValues<LK>())
        {
            if (!map.ContainsKey(key)) missing.Add(key.ToString());
        }

        return missing;
    }
}
