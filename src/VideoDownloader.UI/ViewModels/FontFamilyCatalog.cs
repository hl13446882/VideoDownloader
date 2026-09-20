using System.Globalization;
using System.Windows.Markup;
using System.Windows.Media;

namespace VideoDownloader.UI.ViewModels;

/// <summary>Font list entry: FamilyName is persisted / applied to CSS; DisplayName is localized for the UI.</summary>
public sealed class FontFamilyOption
{
    public FontFamilyOption(string familyName, string displayName)
    {
        FamilyName = familyName;
        DisplayName = displayName;
    }

    public string FamilyName { get; }
    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

internal static class FontFamilyCatalog
{
    private static readonly Dictionary<string, string> ChineseDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft YaHei"] = "微软雅黑",
        ["Microsoft YaHei UI"] = "微软雅黑 UI",
        ["Microsoft JhengHei"] = "微软正黑体",
        ["Microsoft JhengHei UI"] = "微软正黑体 UI",
        ["SimSun"] = "宋体",
        ["NSimSun"] = "新宋体",
        ["SimSun-ExtB"] = "宋体-ExtB",
        ["SimSun-ExtG"] = "宋体-ExtG",
        ["SimHei"] = "黑体",
        ["KaiTi"] = "楷体",
        ["FangSong"] = "仿宋",
        ["STSong"] = "华文宋体",
        ["STHeiti"] = "华文黑体",
        ["STKaiti"] = "华文楷体",
        ["STFangsong"] = "华文仿宋",
        ["STXihei"] = "华文细黑",
        ["DengXian"] = "等线",
        ["YouYuan"] = "幼圆",
        ["LiSu"] = "隶书",
        ["Xingkai"] = "行楷",
        ["FZShuTi"] = "方正舒体",
        ["FZYaoti"] = "方正姚体",
    };

    public static IReadOnlyList<FontFamilyOption> Build()
    {
        var zh = XmlLanguage.GetLanguage("zh-CN");
        var zhHans = XmlLanguage.GetLanguage("zh-Hans");
        var list = new List<FontFamilyOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var family in Fonts.SystemFontFamilies)
        {
            var source = family.Source?.Trim();
            if (string.IsNullOrWhiteSpace(source) || !seen.Add(source))
                continue;

            var display = ResolveDisplayName(family, source, zh, zhHans);
            list.Add(new FontFamilyOption(source, display));
        }

        return list
            .OrderBy(x => x.DisplayName, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))
            .ToArray();
    }

    private static string ResolveDisplayName(
        FontFamily family,
        string source,
        XmlLanguage zh,
        XmlLanguage zhHans)
    {
        if (ChineseDisplayNames.TryGetValue(source, out var mapped))
            return mapped;

        if (family.FamilyNames.TryGetValue(zh, out var zhName) && !string.IsNullOrWhiteSpace(zhName))
            return zhName.Trim();
        if (family.FamilyNames.TryGetValue(zhHans, out var hansName) && !string.IsNullOrWhiteSpace(hansName))
            return hansName.Trim();

        foreach (var name in family.FamilyNames.Values)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var trimmed = name.Trim();
            if (!string.Equals(trimmed, source, StringComparison.OrdinalIgnoreCase) && ContainsCjk(trimmed))
                return trimmed;
        }

        return source;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF')
                return true;
        }

        return false;
    }
}
