using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Json;
using VideoDownloader.Infrastructure.Logging;

namespace VideoDownloader.Infrastructure.Detection.Sites.TikTok;

public sealed class TikTokYtDlpExtractor : IExternalSiteResolver
{
    private readonly AppOptions _options;
    private readonly ILogger<TikTokYtDlpExtractor> _logger;
    private bool? _available;

    public TikTokYtDlpExtractor(IOptions<AppOptions> options, ILogger<TikTokYtDlpExtractor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string? LastError { get; private set; }

    public bool LastFailureIsHumanVerification { get; private set; }

    public bool IsAvailable
    {
        get
        {
            _available ??= CheckAvailable();
            return _available.Value && _options.ExternalResolvers.Enabled;
        }
    }

    private static bool SupportsHost(Uri pageUrl) => pageUrl.Host.Contains("tiktok", StringComparison.OrdinalIgnoreCase);

    public bool SupportsSite(string siteId) => string.Equals(siteId, SiteIds.TikTok, StringComparison.Ordinal);

    public async Task<IReadOnlyList<DetectedVideo>> ResolveAsync(
        Uri pageUrl,
        RequestContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastError = null;
        LastFailureIsHumanVerification = false;
        if (!IsAvailable)
        {
            LastError = "yt-dlp 不可用";
            return [];
        }

        if (!SupportsHost(pageUrl))
        {
            LastError = "TikTokYtDlpExtractor refuses foreign hosts";
            return [];
        }

        var siteId = SiteIds.TikTok;

        if (siteId is not SiteIds.Generic && !_options.Sites.GetSiteEnabled(siteId))
        {
            LastError = $"站点已禁用：{siteId}";
            return [];
        }

        try
        {
            var path = PathExpander.Expand(_options.ExternalResolvers.YtDlpPath);
            var resolveUrl = CanonicalizePageUrl(pageUrl);
            // Use cookies already on the request context (download 403 recovery attaches live WebView cookies).
            // CaptureCookies/UseBrowserCookies only gates whether the browser auto-harvests into context.
            var cookieFile = await WriteCookieFileAsync(context, pageUrl, ct);
            if (cookieFile is null &&
                siteId is SiteIds.YouTube or SiteIds.Bilibili or SiteIds.TikTok or SiteIds.Douyin)
            {
                _logger.LogInformation(
                    "yt-dlp probing {SiteId} without cookies — may fail bot/412 checks",
                    siteId);
            }

            string?[] clientAttempts = siteId is SiteIds.YouTube
                ?
                [
                    "youtube:player_client=web",
                    "youtube:player_client=android,web",
                    "youtube:player_client=tv_embedded",
                    "youtube:player_client=ios,web",
                    null
                ]
                : [null];

            // Browser-cookie use is opt-in. When it is enabled, retry YouTube without
            // the captured session too because stale sessions can prevent extraction.
            string?[] cookieAttempts = cookieFile is null || siteId is not SiteIds.YouTube
                ? [cookieFile]
                : [cookieFile, null];

            try
            {
                string? authenticatedError = null;
                IReadOnlyList<DetectedVideo>? bestResult = null;
                foreach (var cookies in cookieAttempts)
                {
                    foreach (var extractorArgs in clientAttempts)
                    {
                        var videos = await RunYtDlpOnceAsync(
                            path,
                            resolveUrl,
                            pageUrl,
                            siteId,
                            context,
                            cookies,
                            extractorArgs,
                            ct);
                        if (videos.Count > 0)
                        {
                            if (bestResult is null || DetectionScore(videos) > DetectionScore(bestResult))
                                bestResult = videos;
                            if (HasCompleteAdaptiveSet(videos))
                            {
                                LastError = null;
                                LastFailureIsHumanVerification = false;
                                return videos;
                            }
                        }

                        // Y1: human verification is definitive for this page context —
                        // further player_client swaps in the same context are wasted work.
                        if (LastFailureIsHumanVerification)
                        {
                            _logger.LogInformation(
                                "yt-dlp stopped client polling after human-verification for {SiteId}",
                                siteId);
                            goto Done;
                        }

                        if (cookies is not null && !string.IsNullOrWhiteSpace(LastError))
                            authenticatedError ??= LastError;
                    }
                }

            Done:
                if (bestResult is not null)
                {
                    LastError = null;
                    LastFailureIsHumanVerification = false;
                    return bestResult;
                }

                LastError = authenticatedError ?? LastError;
                return [];
            }
            finally
            {
                TryDelete(cookieFile);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LastFailureIsHumanVerification = IsHumanVerificationError(ex.Message);
            _logger.LogInformation(ex, "yt-dlp resolve failed for {Url}", SanitizedLogger.SanitizeUrl(pageUrl.ToString()));
            return [];
        }
    }

    private async Task<IReadOnlyList<DetectedVideo>> RunYtDlpOnceAsync(
        string path,
        string resolveUrl,
        Uri pageUrl,
        string siteId,
        RequestContext context,
        string? cookieFile,
        string? extractorArgs,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Ignore user yt-dlp.conf format selectors that break -J probes.
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        if (!string.IsNullOrWhiteSpace(extractorArgs))
        {
            psi.ArgumentList.Add("--extractor-args");
            psi.ArgumentList.Add(extractorArgs);
        }

        // TikTok blocks non-browser TLS; --impersonate is the common fix (yt-dlp #14473).
        // Do not combine with a hardcoded User-Agent — it fights impersonate headers.
        psi.ArgumentList.Add("--impersonate");
        psi.ArgumentList.Add("chrome");

        if (!string.IsNullOrWhiteSpace(context.Referer) &&
            siteId is not SiteIds.YouTube)
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add(context.Referer);
        }

        if (cookieFile is not null)
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookieFile);
        }

        psi.ArgumentList.Add(resolveUrl);

        _logger.LogInformation(
            "yt-dlp resolver probing {SiteId}: {Url} ({Args})",
            siteId,
            SanitizedLogger.SanitizeUrl(resolveUrl),
            extractorArgs ?? "default");

        using var process = Process.Start(psi);
        if (process is null)
        {
            LastError = "无法启动 yt-dlp";
            return [];
        }

        Diagnostics.HangProbe.Mark("ytdlp.process.start", $"pid={process.Id} url={SanitizedLogger.SanitizeUrl(resolveUrl)}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var registration = timeout.Token.Register(() =>
        {
            Diagnostics.HangProbe.Mark("ytdlp.kill", $"pid={process.Id}");
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            Diagnostics.HangProbe.Mark("ytdlp.WaitForExit.begin", $"pid={process.Id}");
            await process.WaitForExitAsync(timeout.Token);
            Diagnostics.HangProbe.Mark("ytdlp.WaitForExit.end", $"pid={process.Id} code={process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            Diagnostics.HangProbe.Mark("ytdlp.WaitForExit.canceled", $"pid={process.Id}");
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            LastError = "yt-dlp 单次解析超时（30 秒）";
            return [];
        }
        Diagnostics.HangProbe.Mark("ytdlp.ReadToEnd.begin", $"pid={process.Id}");
        string output;
        string error;
        try
        {
            output = await outputTask.WaitAsync(TimeSpan.FromSeconds(10));
            error = await errorTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Diagnostics.HangProbe.Mark("ytdlp.ReadToEnd.TIMEOUT", $"pid={process.Id}");
            LastError = "yt-dlp 输出读取超时";
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return [];
        }
        Diagnostics.HangProbe.Mark("ytdlp.ReadToEnd.end", $"outLen={output.Length} errLen={error.Length}");
        ct.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested)
        {
            LastError = "yt-dlp 单次解析超时（30 秒）";
            return [];
        }
        if (process.ExitCode != 0)
        {
            LastError = TrimYtDlpError(error);
            LastFailureIsHumanVerification = IsHumanVerificationError(LastError);
            // Do not attribute human verification to missing cookies (Y1 / shared constraint).
            _logger.LogInformation(
                "yt-dlp resolver returned exit code {ExitCode} for {SiteId}: {Error} humanVerification={Human}",
                process.ExitCode,
                siteId,
                SanitizedLogger.SanitizeMessage(error),
                LastFailureIsHumanVerification);
            return [];
        }

        if (string.IsNullOrWhiteSpace(output) || output.Trim() == "null")
        {
            LastError = "yt-dlp 未返回格式信息";
            return [];
        }

        var videos = ParseJson(output, pageUrl, context, siteId);
        if (videos.Count == 0)
            LastError = "yt-dlp 已返回数据但无可下载格式";

        _logger.LogInformation(
            "yt-dlp resolver produced {Count} video(s) for {SiteId}: {Url}",
            videos.Count,
            siteId,
            SanitizedLogger.SanitizeUrl(resolveUrl));

        return videos;
    }

    private static string CanonicalizePageUrl(Uri pageUrl)
    {
        // TikTok-only: bare /video/{id} 404s. Keep /@user/video/{id} and /embed/v2/{id}.
        var userVideo = System.Text.RegularExpressions.Regex.Match(
            pageUrl.AbsolutePath,
            @"^/@(?<user>[^/]+)/video/(?<id>\d{10,})/?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (userVideo.Success)
            return $"https://www.tiktok.com/@{userVideo.Groups["user"].Value}/video/{userVideo.Groups["id"].Value}";

        var embed = System.Text.RegularExpressions.Regex.Match(
            pageUrl.AbsolutePath,
            @"^/embed/v2/(?<id>\d{10,})/?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (embed.Success)
            return $"https://www.tiktok.com/embed/v2/{embed.Groups["id"].Value}";

        var bare = System.Text.RegularExpressions.Regex.Match(
            pageUrl.AbsolutePath,
            @"^/video/(?<id>\d{10,})/?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bare.Success)
            return $"https://www.tiktok.com/@tiktok/video/{bare.Groups["id"].Value}";

        foreach (var part in pageUrl.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var key = part[..idx];
            var value = Uri.UnescapeDataString(part[(idx + 1)..]);
            if ((key.Equals("item_id", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("aweme_id", StringComparison.OrdinalIgnoreCase)) &&
                System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{10,}$"))
                return $"https://www.tiktok.com/@tiktok/video/{value}";
        }

        var builder = new UriBuilder(pageUrl) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
    }


    private static string TrimYtDlpError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "yt-dlp 失败（无错误输出）";

        var line = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            ?? error.Trim();
        if (line.Length > 180)
            line = line[..177] + "...";
        return line;
    }

    internal IReadOnlyList<DetectedVideo> ParseJson(
        string json,
        Uri pageUrl,
        RequestContext context,
        string siteId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Video" : "Video";
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var metadata = ExtractMetadata(root);
        var durationSec = JsonNumber.TryDoubleProp(root, "duration", out var dur) && dur > 0
            ? dur
            : (double?)null;

        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return [];

        var variants = new List<MediaVariant>();

        {
            var combined = SelectBestCombined(formats, directOnly: true);
            if (combined.ValueKind == JsonValueKind.Undefined)
                combined = SelectBestCombined(formats, directOnly: false);

            if (combined.ValueKind != JsonValueKind.Undefined)
            {
                var combinedUrl = GetFormatUrl(combined);
                if (!string.IsNullOrWhiteSpace(combinedUrl))
                {
                    var height = JsonNumber.TryInt32Prop(combined, "height", out var chv)
                        ? chv
                        : (int?)null;
                    var container = combined.TryGetProperty("ext", out var cext) ? cext.GetString() : "mp4";
                    var codec = combined.TryGetProperty("vcodec", out var cvc) ? cvc.GetString() : null;
                    variants.Add(MediaVariant.FromCombinedTrack(
                        height is not null ? $"{height}p" : "default",
                        new Uri(combinedUrl),
                        BuildRequestContext(combined, context),
                        null,
                        height,
                        null,
                        codec,
                        container,
                        TryGetSize(combined)));
                }
            }
        }

        var bestVideo = SelectBestVideo(formats, preferMp4: true, directOnly: true);
        if (bestVideo.ValueKind == JsonValueKind.Undefined)
            bestVideo = SelectBestVideo(formats, preferMp4: false, directOnly: true);
        if (bestVideo.ValueKind == JsonValueKind.Undefined)
            bestVideo = SelectBestVideo(formats, preferMp4: false, directOnly: false);

        var bestAudio = SelectBestAudio(formats, preferM4a: true, directOnly: true);
        if (bestAudio.ValueKind == JsonValueKind.Undefined)
            bestAudio = SelectBestAudio(formats, preferM4a: false, directOnly: true);
        if (bestAudio.ValueKind == JsonValueKind.Undefined)
            bestAudio = SelectBestAudio(formats, preferM4a: false, directOnly: false);

        if (bestVideo.ValueKind != JsonValueKind.Undefined)
        {
            var vUrl = GetFormatUrl(bestVideo);
            if (!string.IsNullOrWhiteSpace(vUrl))
            {
                var height = JsonNumber.TryInt32Prop(bestVideo, "height", out var hv) ? hv : (int?)null;
                var vcodec = bestVideo.TryGetProperty("vcodec", out var vc) ? vc.GetString() : null;
                var videoContainer = bestVideo.TryGetProperty("ext", out var ext) ? ext.GetString() : "mp4";
                var videoContext = BuildRequestContext(bestVideo, context);
                var vTrack = new MediaTrack(
                    "video",
                    MediaTrackKind.Video,
                    new Uri(vUrl),
                    vcodec,
                    videoContainer,
                    null,
                    TryGetSize(bestVideo),
                    videoContext);

                MediaTrack? aTrack = null;
                string? audioContainer = null;
                if (bestAudio.ValueKind != JsonValueKind.Undefined)
                {
                    var aUrl = GetFormatUrl(bestAudio);
                    if (!string.IsNullOrWhiteSpace(aUrl))
                    {
                        audioContainer = bestAudio.TryGetProperty("ext", out var aext) ? aext.GetString() : "m4a";
                        var audioContext = BuildRequestContext(bestAudio, context);
                        aTrack = new MediaTrack(
                            "audio",
                            MediaTrackKind.Audio,
                            new Uri(aUrl),
                            bestAudio.TryGetProperty("acodec", out var ac) ? ac.GetString() : null,
                            audioContainer,
                            null,
                            TryGetSize(bestAudio),
                            audioContext);
                    }
                }

                if (aTrack is not null)
                {
                    var outputContainer = ResolveOutputContainer(videoContainer, audioContainer);
                    variants.Add(MediaVariant.FromTracks(
                        height is not null ? $"{height}p" : "default",
                        null,
                        height,
                        null,
                        outputContainer,
                        [vTrack, aTrack]));
                }
                else if (aTrack is null)
                {
                    variants.Add(MediaVariant.FromCombinedTrack(
                        height is not null ? $"{height}p" : "default",
                        vTrack.SourceUrl,
                        videoContext,
                        null,
                        height,
                        null,
                        vcodec,
                        vTrack.Container,
                        vTrack.ContentLength));
                }
            }
        }

        // Keep a bounded, quality-deduplicated format set. Adaptive services commonly expose
        // the high resolutions as video-only streams, while the low combined stream is merely
        // a playback fallback. Returning only one "best" format loses the user's real choices.
        var audioFormats = SelectAudioFormats(formats).ToArray();
        var preferredAudio = audioFormats.FirstOrDefault();
        foreach (var videoFormat in SelectVideoFormats(formats))
        {
            var videoUrl = GetFormatUrl(videoFormat);
            if (string.IsNullOrWhiteSpace(videoUrl) ||
                variants.Any(v => string.Equals(v.SourceUrl.AbsoluteUri, videoUrl, StringComparison.OrdinalIgnoreCase)))
                continue;

            var height = JsonNumber.TryInt32Prop(videoFormat, "height", out var heightValue) ? heightValue : (int?)null;
            var videoContainer = videoFormat.TryGetProperty("ext", out var ext) ? ext.GetString() : "mp4";
            var videoTrack = new MediaTrack(
                "video-" + FormatId(videoFormat),
                MediaTrackKind.Video,
                new Uri(videoUrl),
                StringProperty(videoFormat, "vcodec"),
                videoContainer,
                TryGetBitrate(videoFormat),
                TryGetSize(videoFormat),
                BuildRequestContext(videoFormat, context));

            if (preferredAudio.ValueKind != JsonValueKind.Undefined &&
                TryBuildAudioTrack(preferredAudio, context, out var audioTrack))
            {
                variants.Add(MediaVariant.FromTracks(
                    $"{height?.ToString() ?? "video"}p-{FormatId(videoFormat)}",
                    null,
                    height,
                    SumBitrate(videoTrack.Bandwidth, audioTrack.Bandwidth),
                    ResolveOutputContainer(videoContainer, audioTrack.Container),
                    [videoTrack, audioTrack]));
            }
            else
            {
                variants.Add(MediaVariant.FromTracks(
                    $"{height?.ToString() ?? "video"}p-{FormatId(videoFormat)}",
                    null,
                    height,
                    videoTrack.Bandwidth,
                    videoTrack.Container,
                    [videoTrack]));
            }
        }

        // Audio must remain independently downloadable even where no video stream was usable.
        foreach (var audioFormat in audioFormats)
        {
            if (!TryBuildAudioTrack(audioFormat, context, out var audioTrack) ||
                variants.Any(v => v.Tracks.All(t => t.Kind == MediaTrackKind.Audio) &&
                                  string.Equals(v.SourceUrl.AbsoluteUri, audioTrack.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
                continue;

            variants.Add(MediaVariant.FromTracks(
                $"audio-{FormatId(audioFormat)}",
                null,
                null,
                audioTrack.Bandwidth,
                audioTrack.Container,
                [audioTrack]));
        }

        variants = variants
            .GroupBy(v => string.Join("|", v.Tracks.Select(t => t.SourceUrl.AbsoluteUri)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
            .ThenByDescending(v => v.Height ?? 0)
            .ToList();

        if (variants.Count == 0)
            return [];

        var family = variants[0].Container is "hls" ? MediaFamily.Hls :
            variants[0].Container is "dash" ? MediaFamily.Dash : MediaFamily.DirectMp4;

        return
        [
            new DetectedVideo(
                DeterministicGuid($"yt-dlp:{siteId}:{id ?? pageUrl.AbsoluteUri}"),
                siteId,
                id,
                title,
                pageUrl,
                family,
                variants,
                false,
                ProbeSource.SiteAdapter,
                "yt-dlp",
                metadata)
            {
                DurationSec = durationSec
            }
        ];
    }

    private static IReadOnlyDictionary<string, string> ExtractMetadata(JsonElement root)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddString(metadata, root, "uploader");
        AddString(metadata, root, "uploader_id");
        AddString(metadata, root, "channel");
        AddString(metadata, root, "channel_id");
        AddString(metadata, root, "creator");
        AddString(metadata, root, "artist");
        AddString(metadata, root, "description");
        AddString(metadata, root, "webpage_url");
        AddString(metadata, root, "id");
        return metadata;
    }

    private static void AddString(Dictionary<string, string> metadata, JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            metadata[key] = value.GetString()!;
        }
    }

    private static string? GetFormatUrl(JsonElement format)
    {
        if (format.TryGetProperty("url", out var url) &&
            url.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(url.GetString()))
            return url.GetString();

        if (format.TryGetProperty("manifest_url", out var manifest) &&
            manifest.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(manifest.GetString()))
            return manifest.GetString();

        return null;
    }

    private static long? TryGetSize(JsonElement format)
    {
        if (JsonNumber.TryInt64Prop(format, "filesize", out var fsv))
            return fsv;

        return JsonNumber.TryInt64Prop(format, "filesize_approx", out var av) ? av : null;
    }

    private static JsonElement SelectBestVideo(JsonElement formats, bool preferMp4, bool directOnly)
    {
        var query = formats.EnumerateArray()
            .Where(f => HasUsableFormatUrl(f, directOnly) &&
                        f.TryGetProperty("vcodec", out var vc) &&
                        vc.ValueKind == JsonValueKind.String &&
                        vc.GetString() is not "none" and not null);

        if (preferMp4)
            query = query.Where(f => StringPropertyEquals(f, "ext", "mp4"));

        return query
            .OrderByDescending(f => JsonNumber.TryInt32Prop(f, "height", out var hv) ? hv : 0)
            .ThenByDescending(f => JsonNumber.TryDoubleProp(f, "tbr", out var tb) ? tb : 0)
            .FirstOrDefault();
    }

    private static JsonElement SelectBestCombined(JsonElement formats, bool directOnly)
    {
        return formats.EnumerateArray()
            .Where(f => HasUsableFormatUrl(f, directOnly) &&
                        f.TryGetProperty("vcodec", out var vc) &&
                        vc.ValueKind == JsonValueKind.String &&
                        vc.GetString() is not "none" and not null &&
                        f.TryGetProperty("acodec", out var ac) &&
                        ac.ValueKind == JsonValueKind.String &&
                        ac.GetString() is not "none" and not null)
            .Where(f => TryGetSize(f) is null or >= MediaResourceSizeFilter.MinProgressiveVideoBytes)
            .OrderByDescending(f => JsonNumber.TryInt32Prop(f, "height", out var hv) ? hv : 0)
            .ThenByDescending(f => TryGetSize(f) ?? 0)
            .ThenByDescending(f => JsonNumber.TryDoubleProp(f, "tbr", out var tb) ? tb : 0)
            .FirstOrDefault();
    }

    private static JsonElement SelectBestAudio(JsonElement formats, bool preferM4a, bool directOnly)
    {
        var query = formats.EnumerateArray()
            .Where(f => HasUsableFormatUrl(f, directOnly) &&
                        f.TryGetProperty("acodec", out var ac) &&
                        ac.ValueKind == JsonValueKind.String &&
                        ac.GetString() is not "none" and not null &&
                        f.TryGetProperty("vcodec", out var vc) &&
                        vc.ValueKind == JsonValueKind.String &&
                        vc.GetString() == "none");

        if (preferM4a)
            query = query.Where(f => StringPropertyEquals(f, "ext", "m4a") || StringPropertyEquals(f, "ext", "mp4"));

        return query
            .OrderByDescending(f => JsonNumber.TryDoubleProp(f, "abr", out var ab) ? ab : 0)
            .FirstOrDefault();
    }

    private static IEnumerable<JsonElement> SelectVideoFormats(JsonElement formats) =>
        formats.EnumerateArray()
            .Where(f => HasUsableFormatUrl(f, directOnly: false) &&
                        HasCodec(f, "vcodec", allowNone: false) &&
                        HasCodec(f, "acodec", allowNone: true))
            .GroupBy(f => JsonNumber.TryInt32Prop(f, "height", out var height) ? height : 0)
            .Select(group => group
                .OrderByDescending(f => HasUsableFormatUrl(f, directOnly: true))
                .ThenByDescending(f => StringPropertyEquals(f, "ext", "mp4"))
                .ThenByDescending(f => TryGetSize(f) ?? 0)
                .ThenByDescending(f => TryGetBitrate(f) ?? 0)
                .First())
            .Where(f => TryGetSize(f) is null or >= MediaResourceSizeFilter.MinProgressiveVideoBytes)
            .OrderByDescending(f => JsonNumber.TryInt32Prop(f, "height", out var height) ? height : 0)
            .ThenByDescending(f => TryGetSize(f) ?? 0)
            .ThenByDescending(f => TryGetBitrate(f) ?? 0)
            .Take(12);

    private static IEnumerable<JsonElement> SelectAudioFormats(JsonElement formats) =>
        formats.EnumerateArray()
            .Where(f => HasUsableFormatUrl(f, directOnly: false) &&
                        HasCodec(f, "acodec", allowNone: false) &&
                        HasCodec(f, "vcodec", allowNone: true))
            .OrderByDescending(f => HasUsableFormatUrl(f, directOnly: true))
            .ThenByDescending(f => StringPropertyEquals(f, "ext", "m4a") || StringPropertyEquals(f, "ext", "mp4"))
            .ThenByDescending(f => TryGetBitrate(f) ?? 0)
            .Take(4);

    private static bool HasCodec(JsonElement format, string name, bool allowNone) =>
        format.TryGetProperty(name, out var codec) &&
        codec.ValueKind == JsonValueKind.String &&
        (allowNone ? codec.GetString() == "none" : codec.GetString() is not "none" and not null);

    private bool TryBuildAudioTrack(JsonElement format, RequestContext fallback, out MediaTrack track)
    {
        var url = GetFormatUrl(format);
        if (string.IsNullOrWhiteSpace(url))
        {
            track = null!;
            return false;
        }

        track = new MediaTrack(
            "audio-" + FormatId(format),
            MediaTrackKind.Audio,
            new Uri(url),
            StringProperty(format, "acodec"),
            StringProperty(format, "ext") ?? "m4a",
            TryGetBitrate(format),
            TryGetSize(format),
            BuildRequestContext(format, fallback));
        return true;
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string FormatId(JsonElement element) =>
        StringProperty(element, "format_id") ?? "stream";

    private static long? TryGetBitrate(JsonElement format) =>
        JsonNumber.TryDoubleProp(format, "tbr", out var bitrate) && bitrate > 0
            ? (long)(bitrate * 1000)
            : JsonNumber.TryDoubleProp(format, "abr", out bitrate) && bitrate > 0
                ? (long)(bitrate * 1000)
                : null;

    private static long? SumBitrate(long? left, long? right) =>
        left is null && right is null ? null : (left ?? 0) + (right ?? 0);

    private RequestContext BuildRequestContext(JsonElement format, RequestContext fallback)
    {
        if (!format.TryGetProperty("http_headers", out var headersElement) ||
            headersElement.ValueKind != JsonValueKind.Object)
            return StripCookiesIfDisabled(fallback);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headersElement.EnumerateObject())
        {
            if (header.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(header.Value.GetString()))
            {
                // T3: never promote raw Cookie headers from extractor JSON into the typed context.
                if (header.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    continue;
                headers[header.Name] = header.Value.GetString()!;
            }
        }

        var referer = headers.TryGetValue("Referer", out var r) ? r : fallback.Referer;
        var origin = headers.TryGetValue("Origin", out var o) ? o : fallback.Origin;
        var userAgent = headers.TryGetValue("User-Agent", out var ua) ? ua : fallback.UserAgent;

        return StripCookiesIfDisabled(new RequestContext(
            Guid.NewGuid(),
            fallback.Version + 1,
            referer,
            origin,
            userAgent,
            headers,
            fallback.Cookies,
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// When browser cookie capture is disabled, tracks must not carry or send cookies (T3).
    /// </summary>
    private RequestContext StripCookiesIfDisabled(RequestContext context)
    {
        if (_options.Browser.CaptureCookies && _options.ExternalResolvers.UseBrowserCookies)
            return context;

        if (context.Cookies.Count == 0 &&
            !context.Headers.Keys.Any(k => k.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            return context;

        var headers = context.Headers
            .Where(h => !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        return context with { Cookies = Array.Empty<BrowserCookie>(), Headers = headers };
    }

    internal static bool IsHumanVerificationError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;
        return error.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("confirm you are not a bot", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("not a bot", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("please sign in", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("HTTP Error 412", StringComparison.OrdinalIgnoreCase) ||
               (error.Contains("412", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("Precondition", StringComparison.OrdinalIgnoreCase)) ||
               error.Contains("bot check", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveOutputContainer(string? videoContainer, string? audioContainer)
    {
        if (StringEquals(videoContainer, "webm") && StringEquals(audioContainer, "webm"))
            return "webm";

        return "mp4";
    }

    private static int DetectionScore(IReadOnlyList<DetectedVideo> videos) => videos
        .SelectMany(video => video.Variants)
        .Select(variant => (variant.Height ?? 0) * 10 +
                           (variant.Tracks.Any(track => track.Kind == MediaTrackKind.Audio) ? 1 : 0))
        .DefaultIfEmpty(0)
        .Max();

    private static bool HasCompleteAdaptiveSet(IReadOnlyList<DetectedVideo> videos) => videos
        .SelectMany(video => video.Variants)
        .Any(variant => variant.Height >= 720 &&
                        variant.Tracks.Any(track => track.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined));

    private static bool StringPropertyEquals(JsonElement element, string propertyName, string expected) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString()?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true;

    private static bool StringEquals(string? actual, string expected) =>
        actual?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasUsableFormatUrl(JsonElement format, bool directOnly)
    {
        string? url = null;
        if (format.TryGetProperty("url", out var urlElement) &&
            urlElement.ValueKind == JsonValueKind.String)
            url = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(url) &&
            format.TryGetProperty("manifest_url", out var manifest) &&
            manifest.ValueKind == JsonValueKind.String)
            url = manifest.GetString();

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (format.TryGetProperty("has_drm", out var drm) &&
            drm.ValueKind is JsonValueKind.True)
            return false;

        if (!directOnly)
            return true;

        var protocol = format.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(protocol) &&
            !protocol.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return false;

        return !url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) &&
               !url.Contains("/manifest/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> WriteCookieFileAsync(
        RequestContext context,
        Uri pageUrl,
        CancellationToken ct)
    {
        if (context.Cookies.Count == 0)
            return null;

        var matching = context.Cookies
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) &&
                        IsCookieAllowedForPage(pageUrl.Host, c.Domain))
            .ToList();
        if (matching.Count == 0)
            return null;

        var path = Path.Combine(Path.GetTempPath(), $"vd-yt-dlp-{Guid.NewGuid():N}.cookies.txt");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        // yt-dlp rejects UTF-8 BOM and malformed Netscape rows.
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync("# Netscape HTTP Cookie File");
        await writer.WriteLineAsync("# This file was generated by VideoDownloader. Do not edit.");
        foreach (var cookie in matching)
        {
            var domain = string.IsNullOrWhiteSpace(cookie.Domain) ? pageUrl.Host : cookie.Domain.Trim();
            if (!domain.StartsWith('.') && !domain.Equals(pageUrl.Host, StringComparison.OrdinalIgnoreCase))
            {
                // Host-only cookies stay as-is; domain cookies need a leading dot for subdomains.
            }

            var includeSubdomains = domain.StartsWith('.') ? "TRUE" : "FALSE";
            var pathValue = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path.Trim();
            var secure = cookie.Secure ? "TRUE" : "FALSE";
            var expires = cookie.Expires is null
                ? "0"
                : Math.Max(0, cookie.Expires.Value.ToUnixTimeSeconds())
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var name = SanitizeCookieField(cookie.Name);
            var value = SanitizeCookieField(cookie.Value);
            if (string.IsNullOrEmpty(name))
                continue;

            // Netscape HttpOnly marker is a domain prefix, not a separate column.
            if (cookie.HttpOnly && !domain.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                domain = "#HttpOnly_" + domain;

            await writer.WriteLineAsync(
                $"{domain}\t{includeSubdomains}\t{pathValue}\t{secure}\t{expires}\t{name}\t{value}".AsMemory(),
                ct);
        }

        await writer.FlushAsync(ct);
        return path;
    }

    private static string SanitizeCookieField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static bool IsCookieAllowedForPage(string host, string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return true;
        if (IsCookieDomainMatch(host, domain))
            return true;

        // YouTube auth cookies often live on google.com.
        if (host.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return IsCookieDomainMatch("youtube.com", domain) ||
                   IsCookieDomainMatch("google.com", domain) ||
                   IsCookieDomainMatch("accounts.google.com", domain);
        }

        if (host.Contains("douyin", StringComparison.OrdinalIgnoreCase))
            return IsCookieDomainMatch("douyin.com", domain) ||
                   IsCookieDomainMatch("iesdouyin.com", domain);

        if (host.Contains("bilibili", StringComparison.OrdinalIgnoreCase))
            return IsCookieDomainMatch("bilibili.com", domain);

        if (host.Contains("tiktok", StringComparison.OrdinalIgnoreCase))
        {
            return IsCookieDomainMatch("tiktok.com", domain) ||
                   IsCookieDomainMatch("tiktokv.com", domain) ||
                   IsCookieDomainMatch("bytedance.com", domain) ||
                   IsCookieDomainMatch("byteoversea.com", domain) ||
                   IsCookieDomainMatch("ibytedtos.com", domain) ||
                   IsCookieDomainMatch("ttlivecdn.com", domain) ||
                   IsCookieDomainMatch("tiktokcdn.com", domain) ||
                   IsCookieDomainMatch("tiktokcdn-us.com", domain);
        }

        return false;
    }

    private static bool IsCookieDomainMatch(string host, string domain)
    {
        var normalized = domain.TrimStart('.');
        return host.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(host, StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary cookie files are best-effort cleanup.
        }
    }

    private static Guid DeterministicGuid(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    private bool CheckAvailable()
    {
        try
        {
            var path = PathExpander.Expand(_options.ExternalResolvers.YtDlpPath);
            if (File.Exists(path))
                return true;

            if (!path.Contains('\\') && !path.Contains('/'))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                return p is not null;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
