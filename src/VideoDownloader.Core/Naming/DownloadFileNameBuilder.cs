using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Naming;

/// <summary>
/// Queue/download stem: caption (文案) first; generic may use page title.
/// Title (原名) hard-capped at 30 text characters; meta suffix is <b>not</b> counted:
/// <c>{原名}_{分}_{P}_{MB|GB}</c> e.g. <c>标题_3分_1080P_256MB</c>.
/// Last-resort fallback title: <c>{host}_{yyyyMMdd}</c>.
/// </summary>
public static partial class DownloadFileNameBuilder
{
    public const int MaxStemLength = 30;
    // Windows reserves ASCII '*'. This full-width equivalent is valid in a filename.
    private const string MiddleEllipsis = "\uFF0A";
    private const long OneGibibyte = 1024L * 1024 * 1024;
    private const long OneMebibyte = 1024L * 1024;

    public static string Build(DetectedVideo video, MediaVariant variant)
    {
        // 1) Prefer 文案 / page title carried on DisplayTitle (meta stripped).
        // 2) Never use SiteContentId / author / container as the stem.
        // 3) Bare transport names (public.mp4) are not captions — fall back to host+date.
        var title = LooksLikeBareMediaFileName(video.DisplayTitle)
            ? string.Empty
            : CleanTitle(video.DisplayTitle);
        if (!IsUsableStemTitle(title))
        {
            title = BuildHostDateResolutionFallback(video.PageUrl, now: null);
        }

        var head = ClampStem(title);
        var meta = FormatMetaSuffix(video.DurationSec, variant.Height, variant.TotalContentLength);
        return head + meta;
    }

    /// <summary>
    /// Filename-only fallback used by <see cref="Build"/>:
    /// <c>{host}_{yyyyMMdd}</c>. Host elided to keep the date within the title budget.
    /// Resolution / duration / size are appended separately via <see cref="FormatMetaSuffix"/>.
    /// </summary>
    public static string BuildHostDateResolutionFallback(
        Uri pageUrl,
        int? height = null,
        DateTimeOffset? now = null)
    {
        // height retained for API compatibility; Build attaches resolution in the meta suffix.
        _ = height;
        var host = pageUrl.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Length > 4)
            host = host[4..];
        host = Sanitize(string.IsNullOrWhiteSpace(host) ? "video" : host);

        var day = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var suffix = "_" + day;
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
    /// Keeps a title stem ≤ <see cref="MaxStemLength"/> text characters (meta suffix excluded).
    /// </summary>
    public static string ClampStem(string stem)
    {
        TrySplitMetaSuffix(stem, out var head, out var meta);
        var sanitized = Sanitize(head);
        var clamped = ElideText(sanitized, MaxStemLength);
        var result = string.IsNullOrWhiteSpace(clamped) ? "video" : clamped;
        return result + meta;
    }

    /// <summary>
    /// Sanitize enqueue/display names without folding the meta suffix into the 30-char title budget.
    /// </summary>
    public static string FinalizeEnqueueStem(string displayName)
    {
        TrySplitMetaSuffix(Sanitize(displayName), out var head, out var meta);
        var clamped = ElideText(head, MaxStemLength);
        if (string.IsNullOrWhiteSpace(clamped))
            clamped = "video";
        return clamped + meta;
    }

    /// <summary>
    /// Keeps the title ≤ <see cref="MaxStemLength"/> when appending a collision suffix; preserves meta.
    /// </summary>
    public static string WithCollisionSuffix(string stem, string suffix8)
    {
        TrySplitMetaSuffix(stem, out var head, out var meta);
        var safeSuffix = suffix8.Length <= 8 ? suffix8 : suffix8[..8];
        var budget = MaxStemLength - 1 - TextLength(safeSuffix);
        if (budget < 1)
            return ElideText(safeSuffix, MaxStemLength) + meta;

        var title = ElideText(Sanitize(head), budget);
        if (string.IsNullOrWhiteSpace(title))
            title = "v";
        return title + "_" + safeSuffix + meta;
    }

    /// <summary>Produces a deterministic, user-readable suffix such as <c>_2</c> or <c>_12</c> on the title; preserves meta.</summary>
    public static string WithSequenceSuffix(string stem, int sequence)
    {
        if (sequence < 2)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        TrySplitMetaSuffix(stem, out var head, out var meta);
        var suffix = "_" + sequence.ToString(CultureInfo.InvariantCulture);
        var budget = MaxStemLength - TextLength(suffix);
        var title = ElideText(Sanitize(head), budget);
        return (string.IsNullOrWhiteSpace(title) ? "video" : title) + suffix + meta;
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
    /// Normalizes a user rename stem: optional current-extension strip, sanitize, clamp title, reserved names.
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
        TrySplitMetaSuffix(value, out var head, out var meta);
        if (IsWindowsReservedDeviceName(head))
            value = ClampStem(head + "_file") + meta;
        return value;
    }

    /// <summary>
    /// Builds <c>_3分_1080P_256MB</c>-style suffix. Missing fields are omitted (no empty segments).
    /// </summary>
    public static string FormatMetaSuffix(double? durationSec, int? height, long? totalBytes)
    {
        var parts = new List<string>(3);
        if (durationSec is > 0)
        {
            var minutes = Math.Max(1, (int)Math.Round(durationSec.Value / 60.0, MidpointRounding.AwayFromZero));
            parts.Add(minutes.ToString(CultureInfo.InvariantCulture) + "分");
        }

        if (height is > 0)
            parts.Add(height.Value.ToString(CultureInfo.InvariantCulture) + "P");

        if (totalBytes is > 0)
            parts.Add(FormatSizeLabel(totalBytes.Value));

        return parts.Count == 0 ? string.Empty : "_" + string.Join("_", parts);
    }

    public static string FormatSizeLabel(long bytes)
    {
        if (bytes >= OneGibibyte)
        {
            var gb = bytes / (double)OneGibibyte;
            return (gb >= 10
                    ? Math.Round(gb, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
                    : gb.ToString("0.#", CultureInfo.InvariantCulture))
                   + "GB";
        }

        var mb = bytes / (double)OneMebibyte;
        if (mb < 1)
        {
            // Keep sub-MB files visible without inventing a KB unit.
            var shown = Math.Max(0.1, Math.Round(mb, 1, MidpointRounding.AwayFromZero));
            return shown.ToString("0.#", CultureInfo.InvariantCulture) + "MB";
        }

        if (mb >= 100)
            return Math.Round(mb, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "MB";

        return mb.ToString("0.#", CultureInfo.InvariantCulture) + "MB";
    }

    /// <summary>
    /// Splits a stem into title + optional <c>_分_P_MB</c> meta so clamps/sequence suffixes only touch the title.
    /// </summary>
    public static bool TrySplitMetaSuffix(string? stem, out string head, out string meta)
    {
        head = stem ?? string.Empty;
        meta = string.Empty;
        if (string.IsNullOrEmpty(stem))
            return false;

        var match = DownloadMetaSuffixRegex().Match(stem);
        if (!match.Success || match.Length == 0)
            return false;

        meta = match.Value;
        head = stem[..^meta.Length];
        return true;
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

        // Prefer non-topic caption text; hashtags/mentions usually bloat Douyin/TikTok titles.
        var withoutTags = HashtagRegex().Replace(value, " ");
        withoutTags = MentionRegex().Replace(withoutTags, " ");
        var cleaned = FinalizeTitleCleanup(withoutTags);

        // If stripping topics left nothing (caption was only #话题…), keep topic bodies as the stem.
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            var topics = ExtractHashtagBodies(value);
            if (topics.Count > 0)
                cleaned = FinalizeTitleCleanup(string.Join(" ", topics));
        }

        return cleaned;
    }

    private static string FinalizeTitleCleanup(string value)
    {
        var cleaned = WhitespaceRegex().Replace(value, " ").Trim();
        // Remove spaces to preserve more title text within the filename budget.
        cleaned = cleaned.Replace(" ", "", StringComparison.Ordinal);
        // Strip detection meta glued onto DisplayTitle (resolution/size/container).
        cleaned = TrailingDetectionMetaRegex().Replace(cleaned, string.Empty);
        // Strip download meta if a prior Build result was reused as a title.
        cleaned = DownloadMetaSuffixRegex().Replace(cleaned, string.Empty);
        // Drop "· 2" multi-card suffixes that are not part of the caption.
        cleaned = MultiCardSuffixRegex().Replace(cleaned, string.Empty);
        return cleaned;
    }

    private static List<string> ExtractHashtagBodies(string value)
    {
        var topics = new List<string>();
        foreach (Match match in HashtagRegex().Matches(value))
        {
            var body = match.Value.TrimStart('#').Trim();
            if (body.Length > 0)
                topics.Add(body);
        }

        return topics;
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
        @"(?:\d{3,4}[pP])?(?:\d+(?:\.\d+)?(?:B|KB|MB|GB))?(?:mp4|webm|mkv|m4a|mka|hls|dash)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDetectionMetaRegex();

    // Download meta: at least one of _N分 / _NP / _NMB|_NGB (order fixed).
    [GeneratedRegex(
        @"(?:_\d+分(?:_\d+[pP])?(?:_\d+(?:\.\d+)?(?:MB|GB))?|_\d+[pP](?:_\d+(?:\.\d+)?(?:MB|GB))?|_\d+(?:\.\d+)?(?:MB|GB))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DownloadMetaSuffixRegex();
}
