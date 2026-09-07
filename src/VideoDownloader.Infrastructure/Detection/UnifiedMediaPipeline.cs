using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Detection;

public sealed class UnifiedMediaPipeline : IMediaDetectionPipeline
{
    private readonly IRequestMessageFactory _requests;
    private readonly IReadOnlyList<IExternalSiteResolver> _externals;
    private readonly AppOptions _options;
    private readonly IManifestResolver? _manifests;
    private readonly SemaphoreSlim _slots = new(3);
    private readonly SemaphoreSlim _primarySlots = new(2);
    private Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, string> _validationErrors = new(StringComparer.Ordinal);
    public string? LastValidationError => _validationErrors.IsEmpty ? null : string.Join("; ", _validationErrors.Values.Distinct());
    private readonly object _gate = new();
    internal Func<Uri, Uri, RequestContext, CancellationToken, Task<Probed?>>? InspectOverride { get; set; }
    private ConcurrentDictionary<string, byte> _pending = new();
    private ConcurrentDictionary<string, Probed> _media = new();
    private ConcurrentDictionary<string, DetectedVideo> _externalVideos = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _generation = new();
    private CancellationTokenSource _validationBudget = new();
    private bool _validationStarted;
    private Uri? _page;
    private string? _title;
    private string? _author;
    private string? _observedIdentity;
    private DetectionSession _session = new();
    public Guid SessionId => _session.Id;
    public bool IsCompleted => _session.Phase == DetectionPhase.Completed;
    public void UpdateCaption(Guid sessionId, string caption)
    {
        lock (_gate)
        {
            if (sessionId!=SessionId || string.IsNullOrWhiteSpace(caption) || caption==_title) return;
            _title=caption.Trim();
            Publish(_generation.Token);
        }
    }
    public IDiscoveryScope? BeginDiscovery(Guid sessionId)
    {
        lock (_gate)
        {
            if (sessionId != SessionId) return null;
            var lease = _session.TryEnter();
            return lease is null ? null : new DiscoveryScope(this, sessionId, lease);
        }
    }

    private sealed class DiscoveryScope(UnifiedMediaPipeline owner, Guid sessionId, IDisposable lease) : IDiscoveryScope
    {
        private bool _disposed;
        public IDiscoveryScope? Fork()
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner.SessionId) return null;
                var child = owner._session.TryEnter(continuation: true);
                return child is null ? null : new DiscoveryScope(owner, sessionId, child);
            }
        }
        public Task ProcessAsync(NormalizedNetworkEvent network, CancellationToken ct)
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner.SessionId) return Task.CompletedTask;
                return owner.ProcessCoreAsync(network, ct, continuation: true);
            }
        }
        public Task SubmitAsync(Uri page, string json, RequestContext context, CancellationToken ct)
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner.SessionId) return Task.CompletedTask;
                return owner.ProbePageCoreAsync(page, null, json, context, ct, continuation: true);
            }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                lease.Dispose();
            }
        }
    }
    public Task CompleteDiscoveryAsync(CancellationToken ct)
    {
        lock (_gate) return _session.CompleteAsync(ct);
    }

    public UnifiedMediaPipeline(
        IRequestMessageFactory requests,
        IEnumerable<IExternalSiteResolver> externals,
        IOptions<AppOptions> options,
        IManifestResolver? manifests = null)
    {
        _requests = requests;
        _externals = (externals ?? []).ToArray();
        _options = options.Value;
        _manifests = manifests;
    }

    public event EventHandler<DetectedVideo>? VideoDetected;
    public event EventHandler<DetectedVideo>? VideoUpdated;
    public event EventHandler<IReadOnlyList<DetectedVideo>>? PageProbed;

    public void Clear()
    {
        lock (_gate) ClearCore();
    }

    private void ClearCore()
    {
        _session.Cancel();
        _session = new();
        _generation.Cancel();
        _validationBudget.Cancel();
        _validationBudget = new();
        _validationStarted = false;
        _generation = new();
        _pending = new();
        _media = new();
        _owners = new(StringComparer.Ordinal);
        _validationErrors = new(StringComparer.Ordinal);
        _externalVideos = new(StringComparer.OrdinalIgnoreCase);
        _page = null;
        _title = null;
        _author = null;
        _observedIdentity = null;
        LastExternalError = null;
    }

    public Task ProcessAsync(NormalizedNetworkEvent e, CancellationToken ct)
    {
        lock (_gate) return ProcessCoreAsync(e, ct);
    }

    private Task ProcessCoreAsync(NormalizedNetworkEvent e, CancellationToken ct, bool continuation = false)
    {
        using var source = _session.TryEnter(continuation);
        if (source is null) return Task.CompletedTask;
        if (e.SessionId != Guid.Empty && e.SessionId != SessionId)
            return Task.CompletedTask;
        if (e.StatusCode is not (200 or 206 or null))
            return Task.CompletedTask;

        var page = e.PageUrl ?? _page;
        if (page is null)
            return Task.CompletedTask;

        // Drop late events from a previous document after navigation / Clear.
        if (_page is not null && e.PageUrl is not null && !SamePage(e.PageUrl, _page))
            return Task.CompletedTask;

        // Bind page early so subsequent events in this cycle have a fallback.
        _page ??= page;

        // YouTube SABR adaptive streaming URLs are not progressive downloads,
        // but keep non-SABR googlevideo /videoplayback candidates.
        if (e.Url.AbsoluteUri.Contains("sabr=1", StringComparison.OrdinalIgnoreCase) &&
            !e.Url.AbsoluteUri.Contains("mime=video", StringComparison.OrdinalIgnoreCase) &&
            !e.Url.AbsoluteUri.Contains("mime=audio", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        // Segments thrash the 3 ffprobe slots and almost never yield a displayable item.
        if (MediaUrlNormalizer.IsLikelySegment(e.Url))
            return Task.CompletedTask;

        if (!IsCandidate(e.Url, e.MimeType, e.ResourceType, e.ContentLength))
            return Task.CompletedTask;

        Queue(e.Url, page, e.RequestContext, e.ContentLength, ct, e.MimeType);
        return Task.CompletedTask;
    }

    public Task ProbePageAsync(Uri pageUrl, string? pageTitle, string? pageScriptJson,
        RequestContext context, CancellationToken ct, bool runExternal = false)
    {
        lock (_gate) return ProbePageCoreAsync(pageUrl, pageTitle, pageScriptJson, context, ct, runExternal);
    }

    private async Task ProbePageCoreAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct,
        bool runExternal = false,
        bool continuation = false)
    {
        ct.ThrowIfCancellationRequested();
        using var work = _session.TryEnter(continuation);
        if (work is null) return;
        _page = pageUrl;

        if (IsDirectMediaPage(pageUrl))
            Queue(pageUrl, pageUrl, context, null, ct);

        if (!string.IsNullOrWhiteSpace(pageScriptJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(pageScriptJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("identity", out var identityEl) &&
                    identityEl.ValueKind == JsonValueKind.String &&
                    identityEl.GetString() is { Length: > 0 } identity)
                    _observedIdentity = identity;
                foreach (var titleField in new[] { "caption" })
                {
                    if (root.TryGetProperty(titleField, out var title) &&
                        title.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(title.GetString()))
                    {
                        _title = title.GetString()!.Trim();
                        break;
                    }
                }
                if (root.TryGetProperty("media", out var media))
                {
                    var primary = root.TryGetProperty("identity",out _);
                    foreach (var item in media.EnumerateArray())
                    {
                        if (Uri.TryCreate(pageUrl, item.GetString(), out var url) &&
                            !MediaUrlNormalizer.IsLikelySegment(url) &&
                            !LooksLikeJunkPath(url))
                        {
                            if (primary && MediaOwnership.ForPage(pageUrl, _observedIdentity) is { } owner)
                                _owners[MediaUrlNormalizer.Normalize(url)] = owner;
                            Queue(url, pageUrl, context, null, ct, primary:primary);
                        }
                    }
                }

                if (root.TryGetProperty("candidates", out var candidates))
                    foreach (var candidate in candidates.EnumerateArray())
                    {
                        if (!candidate.TryGetProperty("url", out var address) || !Uri.TryCreate(address.GetString(), UriKind.Absolute, out var url)) continue;
                        var owner = candidate.TryGetProperty("contentIdentity", out var identityValue) ? identityValue.GetString() : null;
                        if (owner is not null) _owners[MediaUrlNormalizer.Normalize(url)] = owner;
                        var currentOwner = MediaOwnership.ForPage(pageUrl, _observedIdentity);
                        if (owner is not null && currentOwner is not null && owner != currentOwner) continue;
                        Queue(url, pageUrl, context, null, ct, primary: owner is not null && owner == currentOwner);
                    }

                if (root.TryGetProperty("author", out var author) &&
                    author.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(author.GetString()))
                    _author = author.GetString()!.Trim();
            }
            catch (JsonException)
            {
            }
        }

        Publish(_generation.Token);
        if (runExternal)
        {
            // Feed roots are unsupported by yt-dlp; prefer a stable content URL when the
            // current player identity already exposed a concrete id.
            var resolveUrl = ResolveExternalPageUrl(pageUrl, _observedIdentity);
            await TryExternalResolveAsync(pageUrl, context, ct, resolveUrl);
        }
    }

    private static Uri ResolveExternalPageUrl(Uri pageUrl, string? observedIdentity)
    {
        if (string.IsNullOrWhiteSpace(observedIdentity))
            return pageUrl;

        var match = System.Text.RegularExpressions.Regex.Match(
            observedIdentity, @"content:(\d{10,}|BV[\w]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return pageUrl;

        var id = match.Groups[1].Value;
        var host = pageUrl.Host;
        var path = pageUrl.AbsolutePath.TrimEnd('/');
        var isFeedRoot = path is "" or "/" or "/recommend" ||
                         path.Equals("/foryou", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("/jingxuan", StringComparison.OrdinalIgnoreCase);
        if (!isFeedRoot)
            return pageUrl;

            if (host.Contains("tiktok", StringComparison.OrdinalIgnoreCase))
                // Bare /video/{id} 404s on www; yt-dlp accepts the @user/video form with a placeholder user.
                return new Uri($"https://www.tiktok.com/@i/video/{id}");
            if (host.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("iesdouyin", StringComparison.OrdinalIgnoreCase))
                return new Uri($"https://www.douyin.com/video/{id}");

        return pageUrl;
    }

    /// <summary>
    /// Morphological candidate gate (G4). No site-host allowlists.
    /// </summary>
    internal static bool IsCandidate(
        Uri url,
        string? mime,
        string? resourceType = null,
        long? contentLength = null)
    {
        if (url.Scheme is not ("http" or "https"))
            return false;

        if (mime?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true ||
            mime?.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) == true ||
            url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsStrongMediaMime(mime))
            return true;

        if (LooksLikeJunkPath(url))
            return false;

        if (resourceType is not null &&
            resourceType.Equals("Media", StringComparison.OrdinalIgnoreCase))
        {
            return contentLength is null or 0 or >= MediaResourceSizeFilter.MinDisplayBytes;
        }

        var path = url.AbsolutePath;
        if (HasMediaExtension(path))
        {
            // Weak extension evidence: require size when known.
            if (contentLength is > 0 and < MediaResourceSizeFilter.MinDisplayBytes)
                return false;
            return true;
        }

        var full = url.AbsoluteUri;
        var queryHint =
            full.Contains("mime=video", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("mime=audio", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("mimetype=video", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("mimetype=audio", StringComparison.OrdinalIgnoreCase);

        var pathHint =
            full.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("playurl", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("playback", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("play_addr", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("downloadaddr", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("playaddr", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/video/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/audio/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/mediasource/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/upos", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/stream", StringComparison.OrdinalIgnoreCase);

        // A generic progressive-play endpoint shape used by multiple embedded players.
        // Do not accept every /play/ API call: require a media identity or explicit play marker.
        var progressivePlayEndpoint = IsProgressivePlayEndpoint(url);

        if (!queryHint && !pathHint && !progressivePlayEndpoint)
        {
            // Large opaque payloads tagged as Media by the browser are still worth probing.
            if (resourceType is not null &&
                resourceType.Equals("Media", StringComparison.OrdinalIgnoreCase) &&
                contentLength is >= MediaResourceSizeFilter.MinDisplayBytes)
                return true;

            if (mime is not null &&
                mime.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) &&
                contentLength is >= MediaResourceSizeFilter.MinDisplayBytes)
                return true;

            return false;
        }

        // Morphological hints without strong MIME: drop known-small payloads.
        if (contentLength is > 0 and < MediaResourceSizeFilter.MinDisplayBytes)
            return false;

        return true;
    }

    private static bool IsStrongMediaMime(string? mime) =>
        mime is not null &&
        (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
         mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
         mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase));

    private static bool HasMediaExtension(string path) =>
        new[] { ".mp4", ".webm", ".m4a", ".mp3", ".ogg", ".wav", ".m3u8", ".mpd", ".ts", ".m4s", ".flv", ".aac", ".mkv" }
            .Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeJunkPath(Uri url)
    {
        var path = url.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") ||
            path.EndsWith(".gif") || path.EndsWith(".svg") || path.EndsWith(".webp") ||
            path.EndsWith(".ico") || path.EndsWith(".woff") || path.EndsWith(".woff2") ||
            path.EndsWith(".ttf") || path.EndsWith(".css") || path.EndsWith(".js") ||
            path.EndsWith(".html") || path.EndsWith(".htm") || path.EndsWith(".json"))
            return true;

        // Common non-media asset path tokens (morphological, not site-specific).
        return path.Contains("/cover") || path.Contains("/thumb") || path.Contains("/avatar") ||
               path.Contains("/sprite") || path.Contains("/emoji") || path.Contains("/static/image");
    }

    private void Queue(Uri url, Uri page, RequestContext context, long? knownLength, CancellationToken ct, string? mime = null, bool primary = false)
    {
        if (url.Scheme is not ("http" or "https"))
            return;

        // Weak small responses may be junk; strong MIME still goes through stream validation.
        // (ABR/range chunks report tiny Content-Length while the media itself is large).
        var ranged = IsRangedOrVolatileMediaUrl(url);
        if (!ranged && !IsStrongMediaMime(mime) &&
            knownLength is > 0 and < MediaResourceSizeFilter.MinStrongMimeBytes &&
            !url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
            !url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            return;

        // Deduplicate by normalized URL so YouTube/TikTok range/ABR variants share one probe slot.
        var mediaKey = MediaUrlNormalizer.Normalize(url);
        var dedupeKey = (primary ? "primary:" : "network:") + mediaKey;
        var probeUrl = url;

        ct.ThrowIfCancellationRequested();
        var work = _session.TryEnter(continuation: true);
        if (work is null) return;
        var pending = _pending;
        if (!pending.TryAdd(dedupeKey, 0))
        {
            work.Dispose();
            return;
        }

        var generation = _generation.Token;
        if (!_validationStarted)
        {
            _validationStarted = true;
            _validationBudget.CancelAfter(TimeSpan.FromSeconds(90));
        }
        var validationBudget = _validationBudget.Token;
        var results = _media;
        var errors = _validationErrors;
        var slots = primary ? _primarySlots : _slots;
        _ = ProbeAsync();

        async Task ProbeAsync()
        {
            using var validation = CancellationTokenSource.CreateLinkedTokenSource(generation, validationBudget);
            try
            {
                await slots.WaitAsync(validation.Token);
                try
                {
                    var found = await (InspectOverride is { } inspect
                        ? inspect(probeUrl, page, context, validation.Token)
                        : InspectAsync(probeUrl, page, context, validation.Token, mime));
                    if (found is not null && !validation.IsCancellationRequested)
                    {
                        results[mediaKey] = found;
                        errors.TryRemove(mediaKey, out _);
                        Publish(generation);
                    }
                    else
                    {
                        pending.TryRemove(dedupeKey, out _);
                        errors[mediaKey] = "Media validation returned no usable streams";
                    }
                }
                finally
                {
                    slots.Release();
                }
            }
            catch (OperationCanceledException)
            {
                pending.TryRemove(dedupeKey, out _);
                if (!generation.IsCancellationRequested) errors[mediaKey] = "Media validation timed out";
            }
            catch (Exception ex)
            {
                pending.TryRemove(dedupeKey, out _);
                errors[mediaKey] = ex is System.ComponentModel.Win32Exception ? "ffprobe could not be started" : "Media validation failed: " + ex.GetType().Name;
            }
            finally { work.Dispose(); }
        }
    }

    private static bool IsRangedOrVolatileMediaUrl(Uri url)
    {
        var q = url.Query;
        return q.Contains("range=", StringComparison.OrdinalIgnoreCase) ||
               q.Contains("bytes=", StringComparison.OrdinalIgnoreCase) ||
               q.Contains("rn=", StringComparison.OrdinalIgnoreCase) ||
               url.AbsoluteUri.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Probed?> InspectAsync(Uri url, Uri page, RequestContext context, CancellationToken ct, string? mime = null, bool resolveManifest = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var manifestKind = url.AbsolutePath.EndsWith(".m3u8",StringComparison.OrdinalIgnoreCase) || mime?.Contains("mpegurl",StringComparison.OrdinalIgnoreCase)==true ? "hls" :
            url.AbsolutePath.EndsWith(".mpd",StringComparison.OrdinalIgnoreCase) || mime?.Contains("dash+xml",StringComparison.OrdinalIgnoreCase)==true ? "dash" : null;
        if (resolveManifest && manifestKind is not null && _manifests is not null)
            return await InspectManifestAsync(url,page,context,manifestKind,timeout.Token);
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var variant = MediaVariant.FromCombinedTrack("probe", url, context);
        using var request = _requests.Create(variant, HttpMethod.Get, url);
        request.Headers.Remove("Range");
        request.Headers.Remove("If-Range");
        var headers = string.Concat(request.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}\r\n"));
        foreach (var arg in new[]
                 {
                     "-v", "error", "-rw_timeout", "10000000", "-headers", headers,
                     "-show_streams", "-show_format", "-of", "json", url.AbsoluteUri
                 })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        using var stop = timeout.Token.Register(() =>
        {
            try { process.Kill(true); }
            catch (InvalidOperationException) { }
        });

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var json = await stdout;
        await stderr;
        // A plausible URL is still only a candidate; a failed validation is never a valid result.
        if (process.ExitCode != 0) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var streamsEl))
            return null;

        var streams = streamsEl.EnumerateArray().Where(s =>
            !(s.TryGetProperty("disposition",out var disposition) && disposition.TryGetProperty("attached_pic",out var picture) && picture.GetInt32()==1)).ToArray();
        var video = streams.FirstOrDefault(s => Text(s, "codec_type") == "video");
        var audio = streams.FirstOrDefault(s => Text(s, "codec_type") == "audio");
        if (video.ValueKind == JsonValueKind.Undefined && audio.ValueKind == JsonValueKind.Undefined)
            return null;

        var kind = video.ValueKind == JsonValueKind.Undefined
            ? MediaTrackKind.Audio
            : audio.ValueKind == JsonValueKind.Undefined
                ? MediaTrackKind.Video
                : MediaTrackKind.Combined;

        var format = doc.RootElement.GetProperty("format");
        var formatName = Text(format, "format_name") ?? "";
        if (formatName.Contains("image",StringComparison.OrdinalIgnoreCase) ||
            formatName.EndsWith("_pipe",StringComparison.OrdinalIgnoreCase)) return null;
        var container = formatName.Contains("hls", StringComparison.OrdinalIgnoreCase) ? "hls"
            : formatName.Contains("dash", StringComparison.OrdinalIgnoreCase) ? "dash"
            : formatName.Contains("webm", StringComparison.OrdinalIgnoreCase) ? "webm"
            : "mp4";

        long.TryParse(Text(format, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        double.TryParse(Text(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration);
        int? height = video.ValueKind != JsonValueKind.Undefined &&
                      video.TryGetProperty("height", out var h) &&
                      h.ValueKind == JsonValueKind.Number &&
                      h.GetInt32() > 0
            ? h.GetInt32()
            : null;

        // Manifest metadata alone cannot classify an untyped media playlist.
        var isManifest = container is "hls" or "dash" ||
                         url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                         url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
        if (resolveManifest && isManifest && _manifests is not null)
            return await InspectManifestAsync(url,page,context,container,timeout.Token);

        var clean = context with
        {
            Headers = context.Headers
                .Where(h => !h.Key.Equals("Range", StringComparison.OrdinalIgnoreCase) &&
                            !h.Key.Equals("If-Range", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase)
        };

        long.TryParse(Text(format,"bit_rate"),NumberStyles.Integer,CultureInfo.InvariantCulture,out var bitRate);
        var bandwidth = bitRate > 0 ? bitRate : (long?)null;
        return new Probed(
            page,
            new MediaTrack("media", kind, url, Text(kind == MediaTrackKind.Audio ? audio : video, "codec_name"), container, bandwidth, size > 0 ? size : null, clean) { IsValidated = true },
            duration,
            height);
    }

    private static bool IsStrongProgressiveUrl(Uri url)
    {
        var full = url.AbsoluteUri;
        return full.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("playurl", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("mime=video", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("mimetype=video", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("playaddr", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("play_addr", StringComparison.OrdinalIgnoreCase) ||
               IsProgressivePlayEndpoint(url);
    }

    private static bool IsProgressivePlayEndpoint(Uri url)
    {
        if (!url.AbsolutePath.Contains("/play/", StringComparison.OrdinalIgnoreCase))
            return false;

        var query = url.Query;
        return query.Contains("video_id=", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("file_id=", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("is_play_url=", StringComparison.OrdinalIgnoreCase);
    }

    public string? LastExternalError { get; private set; }

    private async Task TryExternalResolveAsync(Uri pageUrl, RequestContext context, CancellationToken ct, Uri? resolveUrl = null)
    {
        ct.ThrowIfCancellationRequested();
        LastExternalError = null;
        if (!_options.ExternalResolvers.Enabled)
        {
            LastExternalError = "外置解析已关闭";
            return;
        }

        var target = resolveUrl ?? pageUrl;
        var generation = _generation.Token;
        var results = _externalVideos;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(generation, ct);
        var token = cancellation.Token;
        var anyAvailable = false;
        foreach (var resolver in _externals.Where(r => r.IsAvailable))
        {
            anyAvailable = true;
            if (generation.IsCancellationRequested)
                return;

            try
            {
                var videos = await resolver.ResolveAsync(target, context, token);
                lock (_gate)
                {
                token.ThrowIfCancellationRequested();
                // Bind to the browser document page, not the rewritten content permalink.
                if (_page is null || !SamePage(pageUrl, _page))
                    return;
                if (!string.IsNullOrWhiteSpace(resolver.LastError))
                    LastExternalError = resolver.LastError;

                if (videos.Count == 0)
                    continue;

                foreach (var video in videos)
                {
                    var owner = MediaOwnership.ForPage(pageUrl, _observedIdentity);
                    if (owner?.StartsWith("id:", StringComparison.Ordinal) == true &&
                        !string.IsNullOrWhiteSpace(video.SiteContentId) && owner != "id:" + video.SiteContentId)
                        continue;
                    var filtered = MediaResourceSizeFilter.FilterForDisplay(video);
                    if (filtered.Variants.Count == 0)
                        continue;

                    results[pageUrl.AbsoluteUri] = filtered with { PageUrl = pageUrl };
                }

                LastExternalError = null;
                Publish(generation);
                return;
                }
            }
            catch (OperationCanceledException) when (generation.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                if (generation.IsCancellationRequested || _page is null || !SamePage(pageUrl, _page))
                    return;
                ct.ThrowIfCancellationRequested();
                LastExternalError = ex.Message;
                }
            }
        }

        if (!anyAvailable && LastExternalError is null)
            LastExternalError = "未找到可用的 yt-dlp";
    }

    private void Publish(CancellationToken generation)
    {
        lock (_gate) PublishCore(generation);
    }

    private void PublishCore(CancellationToken generation)
    {
        if (generation.IsCancellationRequested)
            return;

        var page = _page;
        var owner = page is null ? null : MediaOwnership.ForPage(page, _observedIdentity);
        var probed = _media.Select(entry => entry.Value with
            { Track = entry.Value.Track with { ContentIdentity = _owners.GetValueOrDefault(entry.Key) ?? entry.Value.Track.ContentIdentity } })
            .Where(m => page is null || SamePage(m.Page, page))
            .Where(m => owner is null || m.Track.ContentIdentity is null || m.Track.ContentIdentity == owner)
            .ToArray();

        DetectedVideo? external = null;
        if (page is not null && _externalVideos.TryGetValue(page.AbsoluteUri, out var ext))
            external = ext;
        else if (page is not null)
            external = _externalVideos.Values.FirstOrDefault(v => SamePage(v.PageUrl, page));

        // Reconcile complementary tracks across sources before constructing selectable variants.
        if (external is not null && owner is not null)
        {
            var identifiedUrls = external.Variants.SelectMany(v => v.Tracks)
                .Select(t => MediaUrlNormalizer.Normalize(t.SourceUrl)).ToHashSet(StringComparer.Ordinal);
            probed = probed.Select(p => p.Track.ContentIdentity is null && identifiedUrls.Contains(MediaUrlNormalizer.Normalize(p.Track.SourceUrl))
                ? p with { Track = p.Track with { ContentIdentity = owner } } : p).ToArray();
            var externalTracks = external.Variants.Where(v => v.Tracks.Count == 1 || v.Tracks.All(t => t.Container is not ("hls" or "dash")))
                .SelectMany(v => v.Tracks.Select(t => new Probed(external.PageUrl,
                t with { ContentIdentity = owner }, 0, v.Height)))
                .DistinctBy(p => (p.Track.SourceUrl.AbsoluteUri, p.Track.TrackId, p.Track.Kind));
            probed = probed.Concat(externalTracks).ToArray();
        }
        DetectedVideo? local = probed.Length > 0 ? BuildAggregatedVideo(probed, external?.DisplayTitle) : null;

        var merged = MergeVideos(local, external);
        if (merged is null || merged.Variants.Count == 0)
            return;
        merged = merged with { Variants = MediaVariantReconciler.RemoveSupersededVideoOnly(merged.Variants) };

        if (generation.IsCancellationRequested)
            return;

        // Stable id per page so UI replaces in place instead of stacking historical cards.
        if (page is not null)
        {
            merged = merged with { VideoId = _session.Id, SessionId = _session.Id };
        }

        if (!string.IsNullOrWhiteSpace(_title))
            merged = merged with { DisplayTitle = _title };

        VideoDetected?.Invoke(this, merged);
        VideoUpdated?.Invoke(this, merged);
        PageProbed?.Invoke(this, [merged]);
    }

    private DetectedVideo BuildAggregatedVideo(IReadOnlyList<Probed> all, string? extractedCaption)
    {
        var page = all[0].Page;
        var sourceName = Path.GetFileName(all.Select(a => a.Track.SourceUrl.AbsolutePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? "");
        // Never fall back to the bare host: feed pages share one host and that title fails
        // caption ownership checks while looking like a real video title.
        // Rebuilding tracks must not replace an existing content caption with a transport filename.
        var title = !string.IsNullOrWhiteSpace(_title)
            ? _title!
            : !string.IsNullOrWhiteSpace(extractedCaption)
                ? extractedCaption
            : IsDirectMediaPage(page) && !string.IsNullOrWhiteSpace(sourceName)
                ? sourceName!
                : string.IsNullOrWhiteSpace(sourceName) ? "视频" : sourceName;

        var variants = all.Where(a => a.Manifest is not null).SelectMany(a => a.Manifest!.Variants)
            .Where(v => v.Tracks.Count > 1).ToList();
        var singleTracks = all.SelectMany(a => a.Manifest is null ? new[] { a } :
            a.Manifest.Variants.Where(v => v.Tracks.Count == 1).Select(v => new Probed(a.Page,
                v.Tracks[0] with { ContentIdentity = a.Track.ContentIdentity ?? v.Tracks[0].ContentIdentity }, 0, v.Height))).ToArray();
        var videos = singleTracks.Where(a => a.Track.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
            .OrderByDescending(a => a.Height ?? 0)
            .ThenByDescending(a => a.Track.ContentLength ?? 0)
            .ToArray();
        var audios = singleTracks.Where(a => a.Track.Kind == MediaTrackKind.Audio)
            .OrderByDescending(a => a.Track.ContentLength ?? a.Track.Bandwidth ?? 0)
            .Concat(singleTracks.Where(a => a.Track.Kind == MediaTrackKind.Combined)
                .Select(a => a with { Track = a.Track with { Kind = MediaTrackKind.Audio, TrackId = "audio-extract", Codec = null } }))
            .ToArray();

        foreach (var item in videos)
        {
            var bestAudio = audios.FirstOrDefault(audio => CanPair(item, audio));
            var track = item.Track;
            if (track.Kind == MediaTrackKind.Combined)
            {
                variants.Add(MediaVariant.FromTracks(
                    $"视频 {FormatHeight(item.Height)}",
                    null,
                    item.Height,
                    track.Bandwidth,
                    track.Container,
                    [track]));
                variants.Add(MediaVariant.FromTracks(
                    $"音轨 {FormatAudioLabel(item)}",
                    null,
                    null,
                    track.Bandwidth,
                    "mka",
                    [track with { Kind = MediaTrackKind.Audio, TrackId = "audio-extract", Codec = null }]));
                continue;
            }

            if (bestAudio is not null && CanPair(item, bestAudio))
            {
                variants.Add(MediaVariant.FromTracks(
                    $"视频 {FormatHeight(item.Height)}",
                    null,
                    item.Height,
                    (track.Bandwidth ?? 0) + (bestAudio.Track.Bandwidth ?? 0),
                    "mkv",
                    [track, bestAudio.Track]));
            }
            else
            {
                variants.Add(MediaVariant.FromTracks(
                    $"视频 {FormatHeight(item.Height)}（{(audios.Length > 0 ? "音轨待匹配" : "无音轨")}）",
                    null,
                    item.Height,
                    track.Bandwidth,
                    track.Container,
                    [track]));
            }
        }

        foreach (var audio in audios.Where(a => a.Track.TrackId != "audio-extract"))
        {
            variants.Add(MediaVariant.FromTracks(
                $"音轨 {FormatAudioLabel(audio)}",
                null,
                null,
                audio.Track.Bandwidth,
                audio.Track.Container ?? "m4a",
                [audio.Track]));
        }

        // Deduplicate: keep best variant per mode + height bucket (avoid ABR history pile-up).
        variants = variants
            .GroupBy(v =>
            {
                var audioOnly = v.Tracks.Count > 0 && v.Tracks.All(t => t.Kind == MediaTrackKind.Audio);
                return audioOnly
                    ? $"a|{v.Container}|{v.SourceUrl.AbsoluteUri}"
                    : $"v|{v.Height ?? 0}|{v.Container}|{v.VideoCodec}|{v.AudioCodec}|{string.Join(',',v.Tracks.Where(t=>t.Kind==MediaTrackKind.Audio).Select(t=>t.TrackId))}";
            }, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .ThenByDescending(v => v.Height ?? 0)
                .First())
            .Where(v => !MediaResourceSizeFilter.ShouldExcludeVariant(v))
            .OrderByDescending(v => v.Height ?? 0)
            .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
            .ToList();

        var family = all.Any(a => a.Track.Container == "hls") ? MediaFamily.Hls
            : all.Any(a => a.Track.Container == "dash") ? MediaFamily.Dash
            : MediaFamily.DirectMp4;

        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("page:" + page.AbsoluteUri)).AsSpan(0, 16));
        return new DetectedVideo(
            id,
            "generic",
            null,
            title,
            page,
            family,
            variants,
            all.Any(a => a.Manifest?.IsDrmProtected == true),
            ProbeSource.Generic,
            Metadata: string.IsNullOrWhiteSpace(_author)
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["author"] = _author });
    }

    private static bool IsDirectMediaPage(Uri page)
    {
        var path = page.AbsolutePath;
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    private static DetectedVideo? MergeVideos(DetectedVideo? local, DetectedVideo? external)
    {
        if (local is null)
            return external is null ? null : MediaResourceSizeFilter.FilterForDisplay(external);
        if (external is null)
            return local.Variants.Count == 0 ? null : local;

        var map = new Dictionary<string, MediaVariant>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in local.Variants)
            map[$"{v.VariantId}|{v.SourceUrl.AbsoluteUri}"] = v;
        foreach (var v in external.Variants)
            map[$"{v.VariantId}|{v.SourceUrl.AbsoluteUri}"] = v;

        var merged = local with
        {
            IsDrmProtected = local.IsDrmProtected || external.IsDrmProtected,
            // Prefer an already-bound player caption over external/page-host placeholders.
            DisplayTitle = PreferDisplayTitle(local.DisplayTitle, external.DisplayTitle),
            Variants = map.Values
                .OrderByDescending(v => v.Height ?? 0)
                .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .ToArray(),
            ProbeSource = ProbeSource.GenericFallback,
            StatusHint = "已合并通用探测与外置解析"
        };
        return MediaResourceSizeFilter.FilterForDisplay(merged);
    }

    private static string PreferDisplayTitle(string local, string external)
    {
        static bool Weak(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            value is "视频" ||
            value.Contains('.', StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal) &&
            (value.EndsWith(".com", StringComparison.OrdinalIgnoreCase) ||
             value.EndsWith(".tv", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("www.", StringComparison.OrdinalIgnoreCase));

        if (!Weak(local)) return local;
        if (!Weak(external)) return external;
        return string.IsNullOrWhiteSpace(local) ? external : local;
    }

    private static bool CanPair(Probed video, Probed audio)
    {
        if (video.Track.ContentIdentity is null || video.Track.ContentIdentity != audio.Track.ContentIdentity || !SamePage(video.Page, audio.Page))
            return false;

        if (video.Duration > 0 && audio.Duration > 0)
        {
            var delta = Math.Abs(video.Duration - audio.Duration);
            var max = Math.Max(video.Duration, audio.Duration);
            return delta <= 3 || delta / max <= 0.15;
        }

        // DASH/segment streams often lack duration — allow same-page complementary tracks.
        return true;
    }

    private static bool SamePage(Uri a, Uri b) =>
        string.Equals(NormalizePage(a), NormalizePage(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Page identity: host + path + content-id query keys only.
    /// Tracking/playlist params must not drop in-flight media after SPA URL rewrites.
    /// </summary>
    private static string NormalizePage(Uri page)
    {
        var builder = new UriBuilder(page) { Fragment = string.Empty };
        var path = builder.Path.TrimEnd('/');
        if (string.IsNullOrEmpty(path))
            path = "/";
        builder.Path = path;

        var identity = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var key = idx >= 0 ? Uri.UnescapeDataString(part[..idx]) : Uri.UnescapeDataString(part);
            if (!PageIdentityQueryKeys.Contains(key))
                continue;
            var value = idx >= 0 ? Uri.UnescapeDataString(part[(idx + 1)..]) : string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                identity[key] = value;
        }

        builder.Query = identity.Count == 0
            ? string.Empty
            : string.Join('&', identity.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
    }

    private static readonly HashSet<string> PageIdentityQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "v", "id", "modal_id", "aweme_id", "video_id", "bvid"
    };

    private static string FormatHeight(int? height) => height is > 0 ? $"{height}p" : "默认";

    private static string FormatAudioLabel(Probed audio)
    {
        if (audio.Track.ContentLength is > 0)
            return FormatBytes(audio.Track.ContentLength.Value);
        if (audio.Track.Bandwidth is > 0)
            return $"{audio.Track.Bandwidth / 1000}kbps";
        if (audio.Duration > 0)
            return $"{audio.Duration:F0}s";
        return "默认";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var i = 0;
        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return $"{size:F1}{units[i]}";
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : null;

    internal async Task<Probed?> InspectManifestAsync(Uri url,Uri page,RequestContext context,string kind,CancellationToken ct)
    {
        var resource = new MediaResource(Guid.NewGuid(),url,MediaType.Unknown,null,"GET",200,null,null,null,page,null,context,
            new Dictionary<string,string>(),DateTimeOffset.UtcNow);
        var resolved = kind == "hls" ? await _manifests!.ResolveHlsAsync(resource,ct) : await _manifests!.ResolveDashAsync(resource,ct);
        var variants = new List<MediaVariant>();
        foreach (var variant in resolved.Variants)
        {
            var tracks = new List<MediaTrack>();
            foreach (var track in variant.Tracks)
            {
                if (track.Kind != MediaTrackKind.Unknown) { tracks.Add(track); continue; }
                var inspected = InspectOverride is { } inspect
                    ? await inspect(track.SourceUrl, page, context, ct)
                    : await InspectAsync(track.SourceUrl, page, context, ct, resolveManifest: false);
                if (inspected is not null)
                    tracks.Add(track with { Kind = inspected.Track.Kind, Codec = inspected.Track.Codec, IsValidated = true });
            }
            if (tracks.Count == variant.Tracks.Count) variants.Add(variant with { Tracks = tracks });
        }
        resolved = resolved with { Variants = variants };
        if (resolved.Variants.Count == 0) return null;
        return new(page,resolved.Variants[0].Tracks[0],0,resolved.Variants.Max(v=>v.Height),resolved);
    }

    internal sealed record Probed(Uri Page, MediaTrack Track, double Duration, int? Height, ManifestResolutionResult? Manifest = null);
}
