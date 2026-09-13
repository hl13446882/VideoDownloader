using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Naming;

/// <summary>
/// Queue/download stem from caption / page title.
/// Caption text is not cleaned: filename only drops illegal path characters, then hard-caps
/// the title head at 30 text characters (keep head, drop tail). Meta suffix is <b>not</b> counted:
/// <c>{原名}_{分}_{P}_{MB|GB}</c> e.g. <c>标题_3分_1080P_256MB</c>.
/// Last-resort fallback title: <c>{host}_{yyyyMMdd}</c>.
/// </summary>
public static partial class DownloadFileNameBuilder
{
    public const int MaxStemLength = 30;
    private const long OneGibibyte = 1024L * 1024 * 1024;
    private const long OneMebibyte = 1024L * 1024;

    public static string Build(DetectedVideo video, MediaVariant variant)
    {
        // 1) Prefer 文案 / page title on DisplayTitle — keep every character except illegal filename chars.
        // 2) Never use SiteContentId / author / container as the stem.
        // 3) Bare transport names (public.mp4) are not captions — fall back to host+date.
        var title = LooksLikeBareMediaFileName(video.DisplayTitle)
            ? string.Empty
            : StripOwnDownloadMetaSuffix(video.DisplayTitle);
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
    /// <c>{host}_{yyyyMMdd}</c>. Host head-truncated to keep the date within the title budget.
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
        var head = TruncateHead(host, Math.Max(1, budget));
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
    /// Illegal characters are filtered; length overflow drops the tail only (head preserved).
    /// </summary>
    public static string ClampStem(string stem)
    {
        TrySplitMetaSuffix(stem, out var head, out var meta);
        var sanitized = Sanitize(head);
        var clamped = TruncateHead(sanitized, MaxStemLength);
        var result = string.IsNullOrWhiteSpace(clamped) ? "video" : clamped;
        return result + meta;
    }

    /// <summary>
    /// Sanitize enqueue/display names without folding the meta suffix into the 30-char title budget.
    /// </summary>
    public static string FinalizeEnqueueStem(string displayName)
    {
        TrySplitMetaSuffix(Sanitize(displayName), out var head, out var meta);
        var clamped = TruncateHead(head, MaxStemLength);
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
            return TruncateHead(safeSuffix, MaxStemLength) + meta;

        var title = TruncateHead(Sanitize(head), budget);
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
        var title = TruncateHead(Sanitize(head), budget);
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
    /// Replaces only the title head of a stem; keeps <c>_分_P_MB</c> meta suffix unchanged.
    /// </summary>
    public static string ReplaceTitleHead(string? currentStem, string? newTitleHead)
    {
        TrySplitMetaSuffix(currentStem, out _, out var meta);
        var head = FilterLiveInput(newTitleHead).Trim().TrimEnd('.');
        var sanitized = Sanitize(head);
        var clamped = TruncateHead(sanitized, MaxStemLength);
        if (string.IsNullOrWhiteSpace(clamped))
            clamped = "video";
        return clamped + meta;
    }

    /// <summary>
    /// Builds <c>_3分_1080P_256MB</c>-style suffix. Duration is always whole minutes.
    /// Missing fields are omitted (no empty segments).
    /// </summary>
    public static string FormatMetaSuffix(double? durationSec, int? height, long? totalBytes)
    {
        var parts = new List<string>(3);
        if (durationSec is > 0)
            parts.Add(FormatDurationMinutes(durationSec.Value) + "分");

        if (height is > 0)
            parts.Add(height.Value.ToString(CultureInfo.InvariantCulture) + "P");

        if (totalBytes is > 0)
            parts.Add(FormatSizeLabel(totalBytes.Value));

        return parts.Count == 0 ? string.Empty : "_" + string.Join("_", parts);
    }

    /// <summary>Rounds seconds to at least 1 minute for the <c>_N分</c> label.</summary>
    public static int FormatDurationMinutes(double durationSec) =>
        Math.Max(1, (int)Math.Round(durationSec / 60.0, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Fills missing <c>_分_P_MB/GB</c> parts without replacing ones already on the stem.
    /// </summary>
    public static string MergeMissingMeta(string stem, double? durationSec, int? height, long? totalBytes)
    {
        TryParseMetaParts(stem, out var head, out var existing);
        if (string.IsNullOrWhiteSpace(head))
            head = "video";

        var duration = existing.Minutes is > 0
            ? existing.Minutes.Value * 60.0
            : durationSec;
        var mergedHeight = existing.Height ?? height;
        var mergedBytes = existing.Bytes ?? totalBytes;
        return head + FormatMetaSuffix(duration, mergedHeight, mergedBytes);
    }

    /// <summary>True when the stem already has duration, resolution, and size suffixes.</summary>
    public static bool HasCompleteMetaSuffix(string? stem) =>
        TryParseMetaParts(stem, out _, out var meta) &&
        meta.Minutes is > 0 &&
        meta.Height is > 0 &&
        meta.Bytes is > 0;

    /// <summary>
    /// Splits a stem into title + parsed <c>_分_P_MB/GB</c> parts.
    /// </summary>
    public static bool TryParseMetaParts(string? stem, out string head, out DownloadNameMeta meta)
    {
        head = stem ?? string.Empty;
        meta = default;
        if (!TrySplitMetaSuffix(stem, out head, out var suffix) || suffix.Length == 0)
            return false;

        int? minutes = null;
        int? height = null;
        long? bytes = null;
        foreach (var part in suffix.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith("分", StringComparison.Ordinal) &&
                int.TryParse(part[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMinutes) &&
                parsedMinutes > 0)
            {
                minutes = parsedMinutes;
                continue;
            }

            if ((part.EndsWith("P", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(part[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight) &&
                parsedHeight > 0)
            {
                height = parsedHeight;
                continue;
            }

            if (TryParseSizeLabel(part, out var parsedBytes))
                bytes = parsedBytes;
        }

        meta = new DownloadNameMeta(minutes, height, bytes);
        return minutes is not null || height is not null || bytes is not null;
    }

    public readonly record struct DownloadNameMeta(int? Minutes, int? Height, long? Bytes);

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

    public static bool TryParseSizeLabel(string? label, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(label))
            return false;

        if (label.EndsWith("GB", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(label[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) &&
            gb > 0)
        {
            bytes = (long)Math.Round(gb * OneGibibyte, MidpointRounding.AwayFromZero);
            return bytes > 0;
        }

        if (label.EndsWith("MB", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(label[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) &&
            mb > 0)
        {
            bytes = (long)Math.Round(mb * OneMebibyte, MidpointRounding.AwayFromZero);
            return bytes > 0;
        }

        return false;
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

    /// <summary>
    /// Remove only a prior <see cref="Build"/> meta suffix if a built filename was reused as title.
    /// Does not alter caption/hashtag/mention text.
    /// </summary>
    private static string StripOwnDownloadMetaSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return DownloadMetaSuffixRegex().Replace(value, string.Empty);
    }

    private static string Sanitize(string value)
    {
        // Filter illegal filename characters only — never strip # @ spaces or caption punctuation.
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

        // Windows rejects trailing dots/spaces in file names; keep interior spaces and underscores.
        var sanitized = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "video" : sanitized;
    }

    private static int TextLength(string value) =>
        new StringInfo(value).LengthInTextElements;

    /// <summary>
    /// Length limit only: keep the head (e.g. 第130集：…), drop the overflowing tail.
    /// </summary>
    private static string TruncateHead(string value, int maxTextElements)
    {
        if (maxTextElements <= 0 || string.IsNullOrEmpty(value))
            return string.Empty;

        var info = new StringInfo(value);
        if (info.LengthInTextElements <= maxTextElements)
            return value.Trim(' ', '.');

        var head = info.SubstringByTextElements(0, maxTextElements).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(head) ? "video" : head;
    }

    // Download meta: at least one of _N分 / _NP / _NMB|_NGB (order fixed).
    [GeneratedRegex(
        @"(?:_\d+分(?:_\d+[pP])?(?:_\d+(?:\.\d+)?(?:MB|GB))?|_\d+[pP](?:_\d+(?:\.\d+)?(?:MB|GB))?|_\d+(?:\.\d+)?(?:MB|GB))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DownloadMetaSuffixRegex();
}
