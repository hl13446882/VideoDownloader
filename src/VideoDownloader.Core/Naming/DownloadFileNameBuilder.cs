using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Naming;

/// <summary>
/// Queue/download stem: caption (文案) first; generic may use page title; optional <c>_{height}p</c> only.
/// Hard-capped at 30 text characters. No size/container/channel/id noise.
/// Last-resort fallback: <c>{host}_{yyyyMMdd}_{height}p</c>.
/// </summary>
public static partial class DownloadFileNameBuilder
{
    public const int MaxStemLength = 30;
    // Windows reserves ASCII '*'. This full-width equivalent is valid in a filename.
    private const string MiddleEllipsis = "\uFF0A";

    public static string Build(DetectedVideo video, MediaVariant variant)
    {
        // 1) Prefer 文案 / page title carried on DisplayTitle (meta stripped).
        // 2) Never use SiteContentId / author / size / container as the stem.
        // 3) Bare transport names (public.mp4) are not captions — fall back to host+date.
        var title = LooksLikeBareMediaFileName(video.DisplayTitle)
            ? string.Empty
            : CleanTitle(video.DisplayTitle);
        if (!IsUsableStemTitle(title))
        {
            title = BuildHostDateResolutionFallback(video.PageUrl, null);
        }

        var quality = variant.Height is > 0
            ? variant.Height.Value.ToString(CultureInfo.InvariantCulture) + "p"
            : null;
        if (quality is null)
            return ClampStem(title);

        var suffix = "_" + quality;
        var budget = MaxStemLength - TextLength(suffix);
        var head = ElideText(title, Math.Max(1, budget));
        if (string.IsNullOrWhiteSpace(head))
            head = "v";
        return ClampStem(head + suffix);
    }

    /// <summary>
    /// Filename-only fallback used by <see cref="Build"/>:
    /// <c>{host}_{yyyyMMdd}</c> or <c>{host}_{yyyyMMdd}_{height}p</c> (no path). Host elided to keep date/quality.
    /// </summary>
    public static string BuildHostDateResolutionFallback(
        Uri pageUrl,
        int? height = null,
        DateTimeOffset? now = null)
    {
        var host = pageUrl.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Length > 4)
            host = host[4..];
        host = Sanitize(string.IsNullOrWhiteSpace(host) ? "video" : host);

        var day = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var suffix = height is > 0
            ? "_" + day + "_" + height.Value.ToString(CultureInfo.InvariantCulture) + "p"
            : "_" + day;
        var budget = MaxStemLength - TextLength(suffix);
        var head = ElideText(host, Math.Max(1, budget));
        if (string.IsNullOrWhiteSpace(head))
            head = "v";
        return head + suffix;
    }

    /// <summary>
    /// Whether cleaned caption/page-title text is usable as a download stem source.
    /// </summary>
    public static bool IsUsableStemTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value is "video" or "视频" or "相册")
            return false;
        if (Regex.IsMatch(value, @"^视频\d+$", RegexOptions.CultureInvariant))
            return false;
        if (value.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".flv", StringComparison.OrdinalIgnoreCase))
            return false;
        if (LooksLikeBareMediaFileName(value))
            return false;
        return true;
    }

    /// <summary>True when the title is just a CDN/transport file name (e.g. <c>public.mp4</c>).</summary>
    public static bool LooksLikeBareMediaFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var first = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return first.Length <= 64 &&
               Regex.IsMatch(first, @"^[\w.-]+\.(mp4|webm|m4a|mp3|aac|mkv)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
        // Strip detection meta glued onto DisplayTitle (resolution/size/container).
        cleaned = TrailingDetectionMetaRegex().Replace(cleaned, string.Empty);
        // Drop "· 2" multi-card suffixes that are not part of the caption.
        cleaned = MultiCardSuffixRegex().Replace(cleaned, string.Empty);
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

    [GeneratedRegex(@"·\d+$")]
    private static partial Regex MultiCardSuffixRegex();

    // height + size + container as appended by MediaDescriptorMapper after spaces are removed.
    [GeneratedRegex(
        @"(?:\d{3,4}p)?(?:\d+(?:\.\d+)?(?:B|KB|MB|GB))?(?:mp4|webm|mkv|m4a|mka|hls|dash)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDetectionMetaRegex();
}
