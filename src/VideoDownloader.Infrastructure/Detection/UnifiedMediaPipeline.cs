using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Core.Sites;

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
    private readonly ConcurrentBag<ProbeCandidateDecision> _probeDecisions = new();
    public IReadOnlyList<ProbeCandidateDecision> LastProbeDecisions => _probeDecisions.ToArray();
    private readonly object _gate = new();
    internal Func<Uri, Uri, RequestContext, CancellationToken, Task<Probed?>>? InspectOverride { get; set; }
    private ConcurrentDictionary<string, byte> _pending = new();
    private ConcurrentDictionary<string, Probed> _media = new();
    private ConcurrentDictionary<string, DetectedVideo> _externalVideos = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Normalized URL → browser-observed progressive media (CDP Media/200/206).</summary>
    private ConcurrentDictionary<string, BrowserObservedMedia> _browserObserved = new(StringComparer.OrdinalIgnoreCase);
    private DetectedVideo? _lastBuilt;
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
            Publish(_generation.Token, forceEmit: true);
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
    public async Task CompleteDiscoveryAsync(CancellationToken ct)
    {
        Task idle;
        lock (_gate)
            idle = _session.CompleteAsync(ct);

        await idle.ConfigureAwait(false);

        lock (_gate)
        {
            if (_session.Phase != DetectionPhase.Completed)
                return;
            if (_lastBuilt is not null)
                EmitBuilt(_lastBuilt);
            else
                PublishCore(_generation.Token, forceEmit: true);
        }
    }

    private readonly VideoDownloader.Infrastructure.Http.MediaAvailabilityValidator? _availability;
    private readonly ILogger<UnifiedMediaPipeline> _logger;
    private readonly ISiteMediaAdapterResolver? _siteAdapters;
    private readonly ICandidateDecisionPolicy _candidatePolicy;

    public event EventHandler<DetectedVideo>? VideoDetected;
    public event EventHandler<DetectedVideo>? VideoUpdated;
    public event EventHandler<IReadOnlyList<DetectedVideo>>? PageProbed;

    public UnifiedMediaPipeline(
        IRequestMessageFactory requests,
        IEnumerable<IExternalSiteResolver> externals,
        IOptions<AppOptions> options,
        IManifestResolver? manifests = null,
        VideoDownloader.Infrastructure.Http.MediaAvailabilityValidator? availability = null,
        ILogger<UnifiedMediaPipeline>? logger = null,
        ISiteMediaAdapterResolver? siteAdapters = null,
        ICandidateDecisionPolicy? candidatePolicy = null)
    {
        _requests = requests;
        _externals = (externals ?? []).ToArray();
        _options = options.Value;
        _manifests = manifests;
        _availability = availability;
        _logger = logger ?? NullLogger<UnifiedMediaPipeline>.Instance;
        _siteAdapters = siteAdapters;
        _candidatePolicy = candidatePolicy ?? new CandidateDecisionPolicy();
    }

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
        _browserObserved = new(StringComparer.OrdinalIgnoreCase);
        while (_probeDecisions.TryTake(out _)) { }
        _page = null;
        _title = null;
        _author = null;
        _observedIdentity = null;
        _lastBuilt = null;
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

        var pageContext = new PageMediaContext(page, _title, _observedIdentity, SessionId, _author);
        var siteAdapter = _siteAdapters?.Resolve(page);
        var genericAdapter = _siteAdapters?.Generic;
        var browserPlay = IsBrowserPlayEvidence(e);
        var specialSite = siteAdapter is not null &&
                          genericAdapter is not null &&
                          !ReferenceEquals(siteAdapter, genericAdapter);

        NetworkCandidateDecision siteDecision = new(NetworkCandidateDecisionKind.Default, siteAdapter?.Name ?? "none");
        NetworkCandidateDecision genericDecision = new(NetworkCandidateDecisionKind.Default, genericAdapter?.Name ?? "generic");
        if (siteAdapter is not null)
            siteDecision = siteAdapter.EvaluateNetworkCandidate(e, pageContext);
        if (genericAdapter is not null && (!specialSite || siteDecision.Kind == NetworkCandidateDecisionKind.Default))
            genericDecision = genericAdapter.EvaluateNetworkCandidate(e, pageContext);
        else if (genericAdapter is null)
        {
            // Fallback when adapters are not injected (unit tests / legacy ctor).
            if (e.Url.AbsoluteUri.Contains("sabr=1", StringComparison.OrdinalIgnoreCase) &&
                !e.Url.AbsoluteUri.Contains("mime=video", StringComparison.OrdinalIgnoreCase) &&
                !e.Url.AbsoluteUri.Contains("mime=audio", StringComparison.OrdinalIgnoreCase))
            {
                genericDecision = new(NetworkCandidateDecisionKind.Reject, "legacy", "sabr");
            }
            else if (IsCandidate(e.Url, e.MimeType, e.ResourceType, e.ContentLength) || browserPlay)
            {
                genericDecision = new(
                    browserPlay ? NetworkCandidateDecisionKind.StrongAccept : NetworkCandidateDecisionKind.Accept,
                    "legacy",
                    browserPlay ? "browser_play" : "morphology",
                    browserPlay ? MediaEvidence.BrowserObserved : MediaEvidence.Heuristic);
            }
            else
            {
                genericDecision = new(NetworkCandidateDecisionKind.Reject, "legacy", "not_candidate");
            }
        }

        var finalDecision = specialSite && siteDecision.Kind != NetworkCandidateDecisionKind.Default
            ? siteDecision
            : _candidatePolicy.Combine(siteDecision, genericDecision);
        if (finalDecision.Kind is NetworkCandidateDecisionKind.StrongAccept or NetworkCandidateDecisionKind.Accept ||
            siteDecision.Kind != NetworkCandidateDecisionKind.Default)
        {
            _logger.LogInformation(
                "CandidateDecision site={Site} adapter={Adapter} page={Page} contentIdentity={Identity} host={Host} resourceType={Type} mime={Mime} status={Status} contentLength={Length} siteDecision={SiteDec} genericDecision={GenDec} finalDecision={Final} evidence={Evidence} reason={Reason}",
                siteAdapter?.Name ?? "none",
                finalDecision.AdapterName,
                page.AbsolutePath,
                pageContext.ObservedIdentity,
                e.Url.Host,
                e.ResourceType,
                e.MimeType,
                e.StatusCode,
                e.ContentLength,
                siteDecision.Kind,
                genericDecision.Kind,
                finalDecision.Kind,
                finalDecision.Evidence,
                finalDecision.Reason);
        }

        if (finalDecision.Kind == NetworkCandidateDecisionKind.Reject)
            return Task.CompletedTask;

        // Segments thrash the 3 ffprobe slots unless a site StrongAccept / browser play overrides.
        var allowSegment = browserPlay ||
                           finalDecision.Kind == NetworkCandidateDecisionKind.StrongAccept ||
                           finalDecision.Evidence == MediaEvidence.BrowserObserved;
        if (MediaUrlNormalizer.IsLikelySegment(e.Url) && !allowSegment)
            return Task.CompletedTask;

        // StrongAccept from TikTok (etc.) must not be dropped by morphology alone.
        if (finalDecision.Kind is not (NetworkCandidateDecisionKind.StrongAccept or NetworkCandidateDecisionKind.Accept) &&
            !IsCandidate(e.Url, e.MimeType, e.ResourceType, e.ContentLength) &&
            !browserPlay)
            return Task.CompletedTask;

        if (browserPlay || finalDecision.Evidence == MediaEvidence.BrowserObserved)
        {
            var key = MediaUrlNormalizer.Normalize(e.Url);
            var ctx = AttachRequestCookie(e.RequestContext, e.RequestHeaders, e.Url);
            if (siteAdapter is not null)
                ctx = siteAdapter.EnrichRequestContext(ctx, e.Url, pageContext);
            _browserObserved[key] = new BrowserObservedMedia(
                e.Url,
                e.MimeType,
                InferKindFromMime(e.MimeType),
                ctx);
            RecordDecision(new("network", KindLabel(InferKindFromMime(e.MimeType)), MediaOwnership.ForPage(page, _observedIdentity),
                "accepted", finalDecision.Reason ?? "browser_observed", e.Url.Host,
                $"adapter={finalDecision.AdapterName};status={e.StatusCode};type={e.ResourceType};mime={e.MimeType}"));
            _logger.LogInformation(
                "Browser-observed media session={Session} host={Host} status={Status} type={Type} mime={Mime} adapter={Adapter}",
                e.SessionId, e.Url.Host, e.StatusCode, e.ResourceType, e.MimeType, finalDecision.AdapterName);
        }

        Queue(e.Url, page, e.RequestContext, e.ContentLength, ct, e.MimeType,
            browserObserved: browserPlay || finalDecision.Evidence == MediaEvidence.BrowserObserved);
        return Task.CompletedTask;
    }

    /// <summary>
    /// CDP already successfully delivered this resource to the playing document.
    /// Stronger accessibility proof than a later out-of-band sample GET.
    /// </summary>
    internal static bool IsBrowserPlayEvidence(NormalizedNetworkEvent e)
    {
        if (e.StatusCode is not (200 or 206))
            return false;

        if (e.ResourceType is not null &&
            e.ResourceType.Equals("Media", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsStrongMediaMime(e.MimeType);
    }

    private static MediaTrackKind InferKindFromMime(string? mime)
    {
        if (mime is null) return MediaTrackKind.Combined;
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return MediaTrackKind.Audio;
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) &&
            !mime.Contains("audio", StringComparison.OrdinalIgnoreCase))
            return MediaTrackKind.Video;
        return MediaTrackKind.Combined;
    }

    private static string KindLabel(MediaTrackKind kind) => kind switch
    {
        MediaTrackKind.Audio => "audio",
        MediaTrackKind.Video => "video",
        _ => "av"
    };

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
            // Y3: only start external resolve for a concrete video identity or stable watch URL.
            if (!ShouldRunExternalResolve(pageUrl, _observedIdentity))
            {
                RecordDecision(new("external", "page", MediaOwnership.ForPage(pageUrl, _observedIdentity),
                    "skipped", "home_or_feed_without_concrete_video", pageUrl.Host));
                _logger.LogInformation("Skipped external resolve on non-concrete page {Host}{Path}", pageUrl.Host, pageUrl.AbsolutePath);
            }
            else
            {
                var resolveUrl = ResolveExternalPageUrlCore(pageUrl, _observedIdentity);
                await TryExternalResolveAsync(pageUrl, context, ct, resolveUrl);
            }
        }
    }

    /// <summary>Y3 — homepage/feed without concrete identity must not invoke video extractors.</summary>
    internal static bool ShouldRunExternalResolve(Uri pageUrl, string? observedIdentity)
    {
        if (MediaAddressRenewal.HasStableContentAddress(pageUrl))
            return true;

        if (!string.IsNullOrWhiteSpace(observedIdentity) &&
            System.Text.RegularExpressions.Regex.IsMatch(
                observedIdentity,
                @"content:(?:tiktok:|douyin:|youtube:|bilibili:)?(\d{10,}|BV[\w]+|[A-Za-z0-9_-]{6,})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private void RecordDecision(ProbeCandidateDecision decision) => _probeDecisions.Add(decision);

    private static Uri ResolveExternalPageUrl(Uri pageUrl, string? observedIdentity)
    {
        // Kept for call sites without adapter injection; prefer instance ResolveExternalPageUrlCore.
        if (string.IsNullOrWhiteSpace(observedIdentity))
            return pageUrl;

        var match = System.Text.RegularExpressions.Regex.Match(
            observedIdentity, @"content:(?:tiktok:)?(\d{10,}|BV[\w]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
            return new Uri($"https://www.tiktok.com/@i/video/{id}");
        if (host.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("iesdouyin", StringComparison.OrdinalIgnoreCase))
            return new Uri($"https://www.douyin.com/video/{id}");

        return pageUrl;
    }

    private Uri ResolveExternalPageUrlCore(Uri pageUrl, string? observedIdentity)
    {
        if (_siteAdapters is not null)
        {
            var adapter = _siteAdapters.Resolve(pageUrl);
            var rewritten = adapter.CanonicalizeExternalPageUrl(pageUrl, observedIdentity);
            if (rewritten is not null)
                return rewritten;
        }

        return ResolveExternalPageUrl(pageUrl, observedIdentity);
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
            // CDP ResourceType=Media is authoritative. Range responses often report a tiny
            // Content-Length for the first window — do not treat that as "not a video".
            return true;
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

    private void Queue(Uri url, Uri page, RequestContext context, long? knownLength, CancellationToken ct, string? mime = null, bool primary = false, bool browserObserved = false)
    {
        if (url.Scheme is not ("http" or "https"))
            return;

        // Weak small responses may be junk; strong MIME / browser play still go through.
        // (ABR/range chunks report tiny Content-Length while the media itself is large).
        var ranged = IsRangedOrVolatileMediaUrl(url);
        if (!browserObserved && !ranged && !IsStrongMediaMime(mime) &&
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
        var observed = browserObserved || _browserObserved.ContainsKey(mediaKey);
        _ = ProbeAsync();

        async Task ProbeAsync()
        {
            using var validation = CancellationTokenSource.CreateLinkedTokenSource(generation, validationBudget);
            try
            {
                await slots.WaitAsync(validation.Token);
                try
                {
                    Probed? found;
                    if (observed && _browserObserved.TryGetValue(mediaKey, out var evidence))
                    {
                        // Browser already fetched this URL successfully — do not ffprobe/re-GET.
                        found = FromBrowserObservation(page, evidence);
                        RecordDecision(new("network", KindLabel(evidence.KindHint), MediaOwnership.ForPage(page, _observedIdentity),
                            "accepted", "browser_observed_promoted", evidence.Url.Host));
                    }
                    else
                    {
                        found = await (InspectOverride is { } inspect
                            ? inspect(probeUrl, page, context, validation.Token)
                            : InspectAsync(probeUrl, page, context, validation.Token, mime));
                    }

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

    private static Probed FromBrowserObservation(Uri page, BrowserObservedMedia evidence)
    {
        var container = evidence.Mime?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true ? "webm" : "mp4";
        var track = new MediaTrack(
            "browser-media",
            evidence.KindHint,
            evidence.Url,
            null,
            container,
            null,
            null,
            evidence.Context)
        {
            IsValidated = true,
            BrowserObserved = true,
            Evidence = MediaEvidence.BrowserObserved
        };
        return new Probed(page, track, 0, null);
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
            RecordDecision(new("external", "page", null, "skipped", "external_disabled", pageUrl.Host));
            return;
        }

        var target = resolveUrl ?? pageUrl;
        var generation = _generation.Token;
        var sessionId = SessionId;
        var results = _externalVideos;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(generation, ct);
        var token = cancellation.Token;
        var anyAvailable = false;
        foreach (var resolver in _externals.Where(r => r.IsAvailable))
        {
            anyAvailable = true;
            if (generation.IsCancellationRequested || sessionId != SessionId)
                return;

            try
            {
                var videos = await resolver.ResolveAsync(target, context, token);
                if (resolver.LastFailureIsHumanVerification)
                {
                    lock (_gate)
                    {
                        if (generation.IsCancellationRequested || sessionId != SessionId || _page is null || !SamePage(pageUrl, _page))
                            return;
                        LastExternalError = $"{ErrorCodes.HumanVerification}: {resolver.LastError}";
                        RecordDecision(new("external", "page", MediaOwnership.ForPage(pageUrl, _observedIdentity),
                            "rejected", ErrorCodes.HumanVerification, target.Host, resolver.LastError));
                    }
                    return;
                }

                if (_availability is not null)
                {
                    var checkedVideos = new List<DetectedVideo>();
                    string? retainedVideoFailure = null;
                    foreach (var video in videos)
                    {
                        if (generation.IsCancellationRequested || sessionId != SessionId)
                            return;
                        var owner = MediaOwnership.ForPage(pageUrl, _observedIdentity);
                        if (owner?.StartsWith("id:", StringComparison.Ordinal) == true &&
                            !string.IsNullOrWhiteSpace(video.SiteContentId) && owner != "id:" + video.SiteContentId)
                        {
                            RecordDecision(new("external", "video", owner, "rejected", "ownership_mismatch", target.Host, video.SiteContentId));
                            continue;
                        }

                        // Prefer browser-play facts over an independent sample GET (TikTok CDN).
                        var stamped = OverlayBrowserObservedVideos(StampBrowserObserved(video));

                        bool StillCurrent() =>
                            !generation.IsCancellationRequested && sessionId == SessionId &&
                            _page is not null && SamePage(pageUrl, _page);

                        var (validated, videoFailure) = await ProbeSampleGate.ValidateAndRecoverAsync(
                            stamped,
                            pageUrl,
                            owner ?? (video.SiteContentId is null ? null : "id:" + video.SiteContentId),
                            context,
                            resolver,
                            _availability,
                            StillCurrent,
                            _logger,
                            RecordDecision,
                            token);
                        if (videoFailure is not null)
                            retainedVideoFailure ??= videoFailure;
                        if (validated is not null)
                            checkedVideos.Add(validated);
                    }

                    videos = checkedVideos;
                    if (videos.Count == 0 && retainedVideoFailure is not null)
                        LastExternalError = retainedVideoFailure == ErrorCodes.Http403
                            ? $"{ErrorCodes.VideoDenied}: {retainedVideoFailure}"
                            : retainedVideoFailure;
                }

                lock (_gate)
                {
                token.ThrowIfCancellationRequested();
                // C1: bind to the browser document page and original session only.
                if (sessionId != SessionId || _page is null || !SamePage(pageUrl, _page))
                    return;
                if (!string.IsNullOrWhiteSpace(resolver.LastError) && LastExternalError is null)
                    LastExternalError = resolver.LastError;

                if (videos.Count == 0)
                {
                    if (LastExternalError is null)
                        LastExternalError = "外置解析未返回可用媒体";
                    RecordDecision(new("external", "page", MediaOwnership.ForPage(pageUrl, _observedIdentity),
                        "rejected", "no_usable_variants", target.Host, LastExternalError));
                    continue;
                }

                var acceptedVideo = false;
                foreach (var video in videos)
                {
                    var owner = MediaOwnership.ForPage(pageUrl, _observedIdentity);
                    if (owner?.StartsWith("id:", StringComparison.Ordinal) == true &&
                        !string.IsNullOrWhiteSpace(video.SiteContentId) && owner != "id:" + video.SiteContentId)
                    {
                        RecordDecision(new("external", "video", owner, "rejected", "ownership_mismatch", target.Host, video.SiteContentId));
                        continue;
                    }

                    var filtered = MediaResourceSizeFilter.FilterForDisplay(video);
                    if (filtered.Variants.Count == 0)
                    {
                        RecordDecision(new("external", "video", owner, "rejected", "size_filter", target.Host));
                        continue;
                    }

                    // Stamp recovery identity on every kept variant.
                    var stamped = filtered with
                    {
                        PageUrl = pageUrl,
                        Variants = filtered.Variants.Select(v => v with
                        {
                            ContentIdentity = v.ContentIdentity ?? owner ??
                                              (filtered.SiteContentId is null ? null : "id:" + filtered.SiteContentId),
                            RecoveryPageUrl = v.RecoveryPageUrl ??
                                              MediaAddressRenewal.RecoveryAddress(pageUrl, owner) ??
                                              (MediaAddressRenewal.HasStableContentAddress(pageUrl) ? pageUrl : null)
                        }).ToArray()
                    };
                    results[pageUrl.AbsoluteUri] = stamped;
                    acceptedVideo = true;
                    var decisionKind = stamped.Availability is MediaAvailabilityKind.AudioOnly or MediaAvailabilityKind.VideoDenied
                        ? "audio_only"
                        : "av";
                    RecordDecision(new("external", decisionKind, owner, "accepted",
                        stamped.Availability.ToString(), target.Host,
                        $"variants={stamped.Variants.Count}"));
                }

                if (!acceptedVideo)
                {
                    LastExternalError ??= "外置解析候选未通过可下载性或视频归属校验";
                    continue;
                }

                // T2: partial audio-only success must retain the video failure reason.
                if (results.TryGetValue(pageUrl.AbsoluteUri, out var kept) &&
                    kept.Availability is MediaAvailabilityKind.AudioOnly or MediaAvailabilityKind.VideoDenied)
                {
                    var fail = kept.Metadata?.GetValueOrDefault("videoFailure") ?? ErrorCodes.VideoDenied;
                    LastExternalError = $"{ErrorCodes.AudioOnly}: {fail}";
                }
                else
                {
                    LastExternalError = null;
                }

                Publish(generation);
                return;
                }
            }
            catch (OperationCanceledException) when (generation.IsCancellationRequested || sessionId != SessionId)
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
                if (generation.IsCancellationRequested || sessionId != SessionId || _page is null || !SamePage(pageUrl, _page))
                    return;
                ct.ThrowIfCancellationRequested();
                LastExternalError = ex.Message;
                RecordDecision(new("external", "page", null, "rejected", "exception", pageUrl.Host, ex.GetType().Name));
                }
            }
        }

        if (!anyAvailable && LastExternalError is null)
            LastExternalError = "未找到可用的 yt-dlp";
    }

    private void Publish(CancellationToken generation, bool forceEmit = false)
    {
        lock (_gate) PublishCore(generation, forceEmit);
    }

    private void EmitBuilt(DetectedVideo merged)
    {
        VideoDetected?.Invoke(this, merged);
        VideoUpdated?.Invoke(this, merged);
        PageProbed?.Invoke(this, [merged]);
    }

    private void PublishCore(CancellationToken generation, bool forceEmit = false)
    {
        if (generation.IsCancellationRequested)
            return;

        var page = _page;
        var owner = page is null ? null : MediaOwnership.ForPage(page, _observedIdentity);
        var probed = _media.Select(entry =>
            {
                var inherited = _owners.GetValueOrDefault(entry.Key)
                                ?? entry.Value.Track.ContentIdentity
                                ?? (owner is not null && owner.StartsWith("id:", StringComparison.Ordinal)
                                    ? owner
                                    : null);
                return entry.Value with
                {
                    Track = entry.Value.Track with { ContentIdentity = inherited }
                };
            })
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
            var recovery = VideoDownloader.Infrastructure.Download.MediaAddressRenewal.RecoveryAddress(page, owner);
            var candidates = merged.Variants;
            merged = merged with { Variants = candidates.Select(v => v with
            {
                ContentIdentity = owner,
                RecoveryPageUrl = recovery,
                Alternatives = owner is null ? [] : v.Alternatives.Concat(candidates.Where(a => a != v &&
                    VideoDownloader.Infrastructure.Download.MediaAddressRenewal.Compatible(v, a))
                    ).DistinctBy(a => string.Join('|', a.Tracks.Select(t => t.SourceUrl.AbsoluteUri)))
                    .Take(4).Select(a => a with { ContentIdentity = owner, RecoveryPageUrl = recovery, Alternatives = [] }).ToArray()
            }).ToArray() };
        }

        if (!string.IsNullOrWhiteSpace(_title))
            merged = merged with { DisplayTitle = _title };

        _lastBuilt = merged;

        // Defer UI emission until discovery completes so the list shows the final fact set once.
        if (!forceEmit && _session.Phase != DetectionPhase.Completed)
            return;

        EmitBuilt(merged);
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
            .Select(g =>
            {
                var ordered = g.OrderByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                    .ThenByDescending(v => v.Height ?? 0).ToArray();
                return ordered[0] with { Alternatives = ordered.Skip(1).Take(4).ToArray() };
            })
            .Where(v => !MediaResourceSizeFilter.ShouldExcludeVariant(v))
            .OrderByDescending(v => v.Tracks.Any(t => t.BrowserObserved))
            .ThenByDescending(v => v.Height ?? 0)
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
                .OrderByDescending(v => v.Tracks.Any(t => t.BrowserObserved))
                .ThenByDescending(v => v.Height ?? 0)
                .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .ToArray(),
            ProbeSource = ProbeSource.GenericFallback,
            StatusHint = PreferPartialHint(local.StatusHint, external.StatusHint, map.Values),
            Availability = ComputeAvailability(map.Values, local.Availability, external.Availability),
            Metadata = MergeMetadata(local.Metadata, external.Metadata)
        };
        return MediaResourceSizeFilter.FilterForDisplay(merged);
    }

    private static string? PreferPartialHint(string? local, string? external, IEnumerable<MediaVariant> variants)
    {
        if (!variants.Any(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)))
            return external ?? local;
        return "已合并通用探测与外置解析";
    }

    private static MediaAvailabilityKind ComputeAvailability(
        IEnumerable<MediaVariant> variants,
        MediaAvailabilityKind local,
        MediaAvailabilityKind external)
    {
        var list = variants.ToArray();
        var hasVideo = list.Any(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined));
        var hasAudio = list.Any(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined));
        if (hasVideo)
            return MediaAvailabilityKind.Complete;
        if (hasAudio)
        {
            if (local is MediaAvailabilityKind.VideoDenied || external is MediaAvailabilityKind.VideoDenied)
                return MediaAvailabilityKind.VideoDenied;
            if (local is MediaAvailabilityKind.AudioOnly || external is MediaAvailabilityKind.AudioOnly)
                return MediaAvailabilityKind.AudioOnly;
            return MediaAvailabilityKind.AudioOnly;
        }

        return MediaAvailabilityKind.Unavailable;
    }

    private static IReadOnlyDictionary<string, string>? MergeMetadata(
        IReadOnlyDictionary<string, string>? local,
        IReadOnlyDictionary<string, string>? external)
    {
        if (local is null) return external;
        if (external is null) return local;
        var map = new Dictionary<string, string>(local, StringComparer.Ordinal);
        foreach (var (k, v) in external)
            map[k] = v;
        return map;
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
        if (!SamePage(video.Page, audio.Page))
            return false;

        var videoId = video.Track.ContentIdentity;
        var audioId = audio.Track.ContentIdentity;
        if (videoId is null || audioId is null || videoId != audioId)
            return false;

        // Matching content identity is authoritative. Douyin/TikTok BGM duration often
        // differs from the clipped video timeline and must not block pairing.
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
                    tracks.Add(track with
                    {
                        Kind = inspected.Track.Kind,
                        Codec = inspected.Track.Codec,
                        IsValidated = true,
                        // Preserve clear-key HLS metadata when probe only classified streams.
                        Hls = track.Hls ?? inspected.Track.Hls
                    });
            }
            if (tracks.Count == variant.Tracks.Count) variants.Add(variant with { Tracks = tracks });
        }
        resolved = resolved with { Variants = variants };
        if (resolved.Variants.Count == 0) return null;
        return new(page,resolved.Variants[0].Tracks[0],0,resolved.Variants.Max(v=>v.Height),resolved);
    }

    private DetectedVideo StampBrowserObserved(DetectedVideo video)
    {
        if (_browserObserved.IsEmpty)
            return video;

        var variants = video.Variants.Select(v =>
        {
            var tracks = v.Tracks.Select(t =>
            {
                if (!TryGetBrowserObserved(t.SourceUrl, out var evidence) || evidence is null)
                    return t;
                return t with
                {
                    IsValidated = true,
                    BrowserObserved = true,
                    Evidence = MediaEvidence.BrowserObserved,
                    // Prefer the CDP request context that already succeeded.
                    RequestContext = evidence.Context
                };
            }).ToArray();
            return v with { Tracks = tracks };
        }).ToArray();
        return video with { Variants = variants };
    }

    /// <summary>
    /// yt-dlp CDN objects often differ from the URL WebView2 actually played.
    /// Inject proven browser video/combined tracks so ProbeSampleGate can accept them
    /// without an out-of-band GET that 403s.
    /// </summary>
    private DetectedVideo OverlayBrowserObservedVideos(DetectedVideo video)
    {
        var browserVideos = _browserObserved.Values
            .Where(o => o.KindHint is MediaTrackKind.Video or MediaTrackKind.Combined)
            .GroupBy(o => MediaUrlNormalizer.Normalize(o.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
        if (browserVideos.Length == 0)
            return video;

        var existing = video.Variants
            .SelectMany(v => v.Tracks)
            .Select(t => MediaUrlNormalizer.Normalize(t.SourceUrl))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var injected = new List<MediaVariant>();
        var index = 0;
        foreach (var observed in browserVideos)
        {
            var key = MediaUrlNormalizer.Normalize(observed.Url);
            if (!existing.Add(key))
                continue;

            var container = observed.Mime?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true
                ? "webm"
                : "mp4";
            var track = new MediaTrack(
                $"browser-video-{index++}",
                observed.KindHint,
                observed.Url,
                null,
                container,
                null,
                null,
                observed.Context)
            {
                IsValidated = true,
                BrowserObserved = true,
                Evidence = MediaEvidence.BrowserObserved,
                ContentIdentity = video.SiteContentId is null ? null : "id:" + video.SiteContentId
            };
            injected.Add(MediaVariant.FromTracks(
                $"视频 (浏览器实播)",
                null,
                null,
                null,
                container,
                [track]) with
            {
                ContentIdentity = track.ContentIdentity,
                RecoveryPageUrl = video.PageUrl
            });
        }

        if (injected.Count == 0)
            return video;

        return video with { Variants = injected.Concat(video.Variants).ToArray() };
    }

    private bool IsBrowserObservedUrl(Uri url) => TryGetBrowserObserved(url, out _);

    private bool TryGetBrowserObserved(Uri url, out BrowserObservedMedia? evidence)
    {
        var key = MediaUrlNormalizer.Normalize(url);
        if (_browserObserved.TryGetValue(key, out evidence))
            return true;

        foreach (var observed in _browserObserved.Values)
        {
            if (MediaUrlNormalizer.IsSameMedia(observed.Url, url) ||
                MediaUrlNormalizer.IsSameSession(observed.Url, url))
            {
                evidence = observed;
                return true;
            }
        }

        evidence = null;
        return false;
    }

    private static RequestContext AttachRequestCookie(
        RequestContext context,
        IReadOnlyDictionary<string, string> requestHeaders,
        Uri url)
    {
        if (context.Cookies.Count > 0)
            return context;
        if (!requestHeaders.TryGetValue("cookie", out var header) &&
            !requestHeaders.TryGetValue("Cookie", out header))
            return context;
        if (string.IsNullOrWhiteSpace(header))
            return context;

        var cookies = ParseCookieHeader(header, url);
        return cookies.Count == 0 ? context : context with { Cookies = cookies };
    }

    private static IReadOnlyList<BrowserCookie> ParseCookieHeader(string header, Uri url)
    {
        var list = new List<BrowserCookie>();
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
                continue;
            var name = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            if (name.Length == 0)
                continue;
            list.Add(new BrowserCookie(name, value, url.Host, "/", null, url.Scheme == "https", false));
        }
        return list;
    }

    internal sealed record BrowserObservedMedia(
        Uri Url,
        string? Mime,
        MediaTrackKind KindHint,
        RequestContext Context);

    internal sealed record Probed(Uri Page, MediaTrack Track, double Duration, int? Height, ManifestResolutionResult? Manifest = null);
}
