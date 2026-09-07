using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Naming;

/// <summary>
/// Short download stem: detected copy + quality, hard-capped at 30 text characters.
/// Extension is applied by the download engine from the real container.
/// </summary>
public static partial class DownloadFileNameBuilder
{
    public const int MaxStemLength = 30;
    // Windows reserves ASCII '*'. This full-width equivalent is valid in a filename.
    private const string MiddleEllipsis = "\uFF0A";

    public static string Build(DetectedVideo video, MediaVariant variant)
    {
        var title = CleanTitle(video.DisplayTitle);
        if (string.IsNullOrWhiteSpace(title))
            title = CleanTitle(video.SiteContentId);
        if (string.IsNullOrWhiteSpace(title))
            title = "video";

        var quality = variant.Height is > 0 ? $"{variant.Height}p" : null;
        var stem = string.IsNullOrWhiteSpace(quality) ? title : title + "_" + quality;
        return ClampStem(stem);
    }

    /// <summary>
    /// Keeps a stem ≤ <see cref="MaxStemLength"/> text characters.
    /// </summary>
    public static string ClampStem(string stem)
    {
        var sanitized = Sanitize(stem);
        var clamped = ElideText(sanitized, MaxStemLength);
        return string.IsNullOrWhiteSpace(clamped) ? "video" : clamped;
    }

    /// <summary>
    /// Keeps a stem ≤ <see cref="MaxStemLength"/> when appending a collision suffix.
    /// </summary>
    public static string WithCollisionSuffix(string stem, string suffix8)
    {
        var safeSuffix = suffix8.Length <= 8 ? suffix8 : suffix8[..8];
        var budget = MaxStemLength - 1 - TextLength(safeSuffix);
        if (budget < 1)
            return ElideText(safeSuffix, MaxStemLength);

        var head = ElideText(Sanitize(stem), budget);
        if (string.IsNullOrWhiteSpace(head))
            head = "v";
        return head + "_" + safeSuffix;
    }

    /// <summary>Produces a deterministic, user-readable suffix such as <c>_2</c> or <c>_12</c>.</summary>
    public static string WithSequenceSuffix(string stem, int sequence)
    {
        if (sequence < 2)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        var suffix = "_" + sequence.ToString(CultureInfo.InvariantCulture);
        var budget = MaxStemLength - TextLength(suffix);
        var head = ElideText(Sanitize(stem), budget);
        return string.IsNullOrWhiteSpace(head) ? "video" + suffix : head + suffix;
    }

    /// <summary>
    /// Live typing filter: drops Windows-illegal filename characters without forcing a fallback name.
    /// </summary>
    public static string FilterLiveInput(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (invalid.Contains(ch) || ch is ':' or '"' or '\'' or '?' or '*' or '<' or '>' or '|' or '/' or '\\')
                continue;
            if (char.IsControl(ch))
                continue;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes a user rename stem: optional current-extension strip, sanitize, clamp, reserved names.
    /// </summary>
    public static string NormalizeRenameStem(string? rawStem, string? currentExtension = null)
    {
        var value = FilterLiveInput(rawStem).Trim().TrimEnd('.');
        if (!string.IsNullOrWhiteSpace(currentExtension))
        {
            var ext = currentExtension.StartsWith('.') ? currentExtension : "." + currentExtension;
            if (value.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                value = value[..^ext.Length].TrimEnd().TrimEnd('.');
        }

        value = ClampStem(value);
        if (IsWindowsReservedDeviceName(value))
            value = ClampStem(value + "_file");
        return value;
    }

    private static bool IsWindowsReservedDeviceName(string stem)
    {
        var name = stem;
        var dot = name.IndexOf('.');
        if (dot >= 0)
            name = name[..dot];
        return name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }

    private static string CleanTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Drop hashtags / mentions that blow up Douyin/TikTok titles.
        var cleaned = HashtagRegex().Replace(value, " ");
        cleaned = MentionRegex().Replace(cleaned, " ");
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        // Remove spaces to preserve more title text within the filename budget.
        cleaned = cleaned.Replace(" ", "", StringComparison.Ordinal);
        return cleaned;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (invalid.Contains(ch) || ch is ':' or '"' or '\'' or '?' or '*' or '<' or '>' or '|' or '/' or '\\')
                continue;
            if (char.IsControl(ch))
                continue;
            builder.Append(ch);
        }

        var sanitized = DuplicateSeparatorRegex().Replace(builder.ToString(), "_").Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "video" : sanitized;
    }

    private static int TextLength(string value) =>
        new StringInfo(value).LengthInTextElements;

    private static string ElideText(string value, int maxTextElements)
    {
        if (maxTextElements <= 0 || string.IsNullOrEmpty(value))
            return string.Empty;

        var info = new StringInfo(value);
        if (info.LengthInTextElements <= maxTextElements)
            return value.Trim(' ', '.', '_');

        if (maxTextElements == 1)
            return MiddleEllipsis;

        var prefixLength = (maxTextElements - 1) / 2;
        var suffixLength = maxTextElements - 1 - prefixLength;
        var prefix = info.SubstringByTextElements(0, prefixLength).Trim(' ', '.', '_');
        var suffix = info.SubstringByTextElements(info.LengthInTextElements - suffixLength, suffixLength)
            .Trim(' ', '.', '_');
        var elided = prefix + MiddleEllipsis + suffix;
        return string.IsNullOrWhiteSpace(elided.Trim(MiddleEllipsis[0])) ? "video" : elided;
    }

    [GeneratedRegex(@"#[^\s#]+")]
    private static partial Regex HashtagRegex();

    [GeneratedRegex(@"@[^\s@]+")]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"_+")]
    private static partial Regex DuplicateSeparatorRegex();
}
