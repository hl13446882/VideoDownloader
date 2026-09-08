using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Sites;
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
    private IReadOnlyList<DetectedVideo>? _lastBuilt;
    private CancellationTokenSource _generation = new();
    private CancellationTokenSource _validationBudget = new();
    private bool _validationStarted;
    private Uri? _page;
    private string? _title;
    private bool _titleFromCaption;
    private string? _author;
    private string? _observedIdentity;
    private List<Uri> _albumImages = [];
    private DetectionSession _session = new();
    public Guid SessionId => _session.Id;
    public bool IsCompleted => _session.Phase == DetectionPhase.Completed;
    public void UpdateCaption(Guid sessionId, string caption)
    {
        lock (_gate)
        {
            if (sessionId!=SessionId || string.IsNullOrWhiteSpace(caption) || caption==_title) return;
            // Do not let transport filenames overwrite a real caption / page title.
            if (IsWeakCaption(caption) && !IsWeakCaption(_title)) return;
            _title=caption.Trim();
            _titleFromCaption = true;
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
            if (_lastBuilt is { Count: > 0 })
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
        _titleFromCaption = false;
        _author = null;
        _observedIdentity = null;
        _lastBuilt = null;
        _albumImages = [];
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

        // Drop CDN leftovers from the previous site after a host change (Bilibili→MacCMS etc.).
        if (IsLikelyCrossSiteLeak(page, e.Url))
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

        // Douyin/TikTok MSE Range windows report tiny Content-Length. Do not treat that length
        // as the object size — but keep browser-play URLs so Overlay/download can still use them.
        var tinyByteDance = IsInsufficientByteDanceDownloadObject(e.Url, e.ContentLength);
        var effectiveLength = tinyByteDance ? null : e.ContentLength;
        if (tinyByteDance &&
            !(browserPlay || finalDecision.Evidence == MediaEvidence.BrowserObserved))
        {
            RecordDecision(new("network", "av", MediaOwnership.ForPage(page, _observedIdentity),
                "rejected", "tiny_mse_slice", e.Url.Host,
                $"length={e.ContentLength};status={e.StatusCode};type={e.ResourceType}"));
            return Task.CompletedTask;
        }

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
            var kindHint = InferKindFromMime(e.MimeType, e.Url);
            if (_browserObserved.TryGetValue(key, out var prev) &&
                (prev.ContentLength ?? 0) > (effectiveLength ?? 0) &&
                effectiveLength is not null)
            {
                // Keep the larger observed object; refresh cookies from the latest successful request.
                _browserObserved[key] = prev with { Context = ctx };
            }
            else
            {
                var lengthToStore = effectiveLength ?? prev?.ContentLength;
                _browserObserved[key] = new BrowserObservedMedia(
                    e.Url,
                    e.MimeType,
                    kindHint,
                    ctx,
                    lengthToStore);
            }
            RecordDecision(new("network", KindLabel(kindHint), MediaOwnership.ForPage(page, _observedIdentity),
                "accepted", tinyByteDance ? "browser_observed_strip_range_length" : (finalDecision.Reason ?? "browser_observed"), e.Url.Host,
                $"adapter={finalDecision.AdapterName};status={e.StatusCode};type={e.ResourceType};mime={e.MimeType};length={e.ContentLength};storedLength={effectiveLength}"));
            _logger.LogInformation(
                "Browser-observed media session={Session} host={Host} status={Status} type={Type} mime={Mime} length={Length} storedLength={Stored} adapter={Adapter}",
                e.SessionId, e.Url.Host, e.StatusCode, e.ResourceType, e.MimeType, e.ContentLength, effectiveLength, finalDecision.AdapterName);
        }

        Queue(e.Url, page, e.RequestContext, effectiveLength, ct, e.MimeType,
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

    internal static MediaTrackKind InferKindFromMime(string? mime, Uri? url = null)
    {
        if (url is not null)
        {
            var full = url.AbsoluteUri;
            var path = url.AbsolutePath;
            if (path.Contains("/media-audio-", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/ies-music/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("-30216", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("-30280", StringComparison.OrdinalIgnoreCase) ||
                full.Contains("mime_type=audio", StringComparison.OrdinalIgnoreCase) ||
                full.Contains("mimetype=audio", StringComparison.OrdinalIgnoreCase) ||
                full.Contains("/audio/tos/", StringComparison.OrdinalIgnoreCase) ||
                full.Contains("media_type=audio", StringComparison.OrdinalIgnoreCase))
                return MediaTrackKind.Audio;
            // Separate video-only DASH/fMP4 track.
            if (path.Contains("/media-video-", StringComparison.OrdinalIgnoreCase))
                return MediaTrackKind.Video;
            // Muxed progressive Douyin/TikTok objects often advertise mime_type=video_mp4
            // without /media-video- — treat as Combined so we do not invent a live audio pair.
            if ((full.Contains("mime_type=video", StringComparison.OrdinalIgnoreCase) ||
                 full.Contains("mimetype=video", StringComparison.OrdinalIgnoreCase) ||
                 full.Contains("/video/tos/", StringComparison.OrdinalIgnoreCase) ||
                 full.Contains("media_type=video", StringComparison.OrdinalIgnoreCase)) &&
                !IsDouyinLiveStream(url))
                return MediaTrackKind.Combined;
            if (path.Contains("-300", StringComparison.OrdinalIgnoreCase))
                return MediaTrackKind.Video;
        }

        if (mime is null) return MediaTrackKind.Combined;
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return MediaTrackKind.Audio;
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) &&
            !mime.Contains("audio", StringComparison.OrdinalIgnoreCase))
            return MediaTrackKind.Video;
        return MediaTrackKind.Combined;
    }

    /// <summary>Douyin feed live previews (HLS/FLV) must not attach to short-video VOD.</summary>
    public static bool IsDouyinLiveStream(Uri url)
    {
        var host = url.Host;
        if (host.Contains("douyinliving", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("livehwc", StringComparison.OrdinalIgnoreCase))
            return true;
        var full = url.AbsoluteUri;
        return full.Contains("pull-hls", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("pull-flv", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/media/stream-", StringComparison.OrdinalIgnoreCase) ||
               full.Contains("/third/stream-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>www.douyin.com/aweme/v1/play gateways 302 to CDN — prefer direct objects.</summary>
    public static bool IsDouyinPlayGateway(Uri url)
    {
        var host = url.Host;
        if (!host.Equals("www.douyin.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("www.iesdouyin.com", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = url.AbsolutePath;
        return path.Contains("/aweme/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/play/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Douyin/TikTok CDN hosts only. Unknown-size objects are allowed; known sub-display
    /// lengths are MSE Range windows, not full progressive/adaptive tracks.
    /// </summary>
    public static bool IsByteDanceOrTikTokMediaCdn(Uri url)
    {
        var host = url.Host;
        if (SiteNetworkHelper.IsDouyinHost(url) || SiteNetworkHelper.IsTikTokHost(url))
            return true;
        return host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("byteicdn", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("iesdouyin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when Douyin/TikTok CDN reports a known payload smaller than a downloadable object.
    /// Video/Combined use display size; audio uses a lower floor so short BGM is kept while
    /// MSE Range crumbs (~1KiB) are still dropped. Does not apply to other sites.
    /// </summary>
    public static bool IsInsufficientByteDanceDownloadObject(Uri url, long? contentLength)
    {
        if (contentLength is null or <= 0)
            return false;
        if (!IsByteDanceOrTikTokMediaCdn(url))
            return false;

        var kind = InferKindFromMime(null, url);
        var min = kind == MediaTrackKind.Audio
            ? MediaResourceSizeFilter.MinStrongMimeBytes
            : MediaResourceSizeFilter.MinDisplayBytes;
        return contentLength < min;
    }

    internal static string? ExtractContentIdFromUrl(Uri url)
    {
        var q = url.Query;
        foreach (var key in new[] { "__vid=", "target=", "aweme_id=", "item_ids=", "item_id=", "modal_id=" })
        {
            var idx = q.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + key.Length;
            var end = q.IndexOf('&', start);
            var raw = Uri.UnescapeDataString(end < 0 ? q[start..] : q[start..end]);
            if (Regex.IsMatch(raw, @"^\d{10,}$"))
                return raw;
        }
        return null;
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
                        var text = title.GetString()!.Trim();
                        if (!IsWeakCaption(text))
                        {
                            _title = text;
                            _titleFromCaption = true;
                        }
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
                            !LooksLikeJunkPath(url) &&
                            !LooksLikeImageCdn(url) &&
                            !IsLikelyCrossSiteLeak(pageUrl, url))
                        {
                            if (primary && MediaOwnership.ForPage(pageUrl, _observedIdentity) is { } owner)
                                _owners[MediaUrlNormalizer.Normalize(url)] = owner;
                            Queue(url, pageUrl, context, null, ct, primary:primary);
                        }
                    }
                }

                // Douyin/TikTok photo mode: still images + BGM compose into one slideshow variant.
                _albumImages = [];
                if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in images.EnumerateArray())
                    {
                        if (Uri.TryCreate(pageUrl, item.GetString(), out var imageUrl) &&
                            imageUrl.Scheme is "http" or "https")
                            _albumImages.Add(imageUrl);
                    }
                    if (_albumImages.Count > 0)
                        _logger.LogInformation("Album images discovered count={Count} page={Host}", _albumImages.Count, pageUrl.Host);
                }

                if (root.TryGetProperty("candidates", out var candidates))
                    foreach (var candidate in candidates.EnumerateArray())
                    {
                        if (!candidate.TryGetProperty("url", out var address) || !Uri.TryCreate(address.GetString(), UriKind.Absolute, out var url)) continue;
                        if (LooksLikeJunkPath(url) || LooksLikeImageCdn(url) || !IsCandidate(url, null))
                            continue;
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

        // Generic pages: document.title is an allowed caption fallback (never transport filenames).
        if (IsWeakCaption(_title) && !IsWeakCaption(pageTitle))
            _title = pageTitle!.Trim();

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

        if (LooksLikeJunkPath(url) || LooksLikeImageCdn(url))
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
            path.Contains("/stream", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/m3u8", StringComparison.OrdinalIgnoreCase);

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

    private static bool IsManifestAddress(Uri url, string? mime) =>
        url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
        mime?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true ||
        mime?.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) == true;

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
               path.Contains("/sprite") || path.Contains("/emoji") || path.Contains("/emoticon") ||
               path.Contains("/static/image") || path.Contains("/im-") ||
               path.Contains("/aweme-image") || path.Contains("/obj/tos-cn-i-");
    }

    private static bool LooksLikeImageCdn(Uri url)
    {
        var host = url.Host;
        return host.Contains("byteimg", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("douyinpic", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("tiktokcdn-us.com", StringComparison.OrdinalIgnoreCase) &&
               url.AbsolutePath.Contains("/image", StringComparison.OrdinalIgnoreCase);
    }

    private void Queue(Uri url, Uri page, RequestContext context, long? knownLength, CancellationToken ct, string? mime = null, bool primary = false, bool browserObserved = false)
    {
        if (url.Scheme is not ("http" or "https"))
            return;

        // Douyin/TikTok: never promote known-tiny MSE slices, even when CDP marked them Media.
        if (IsInsufficientByteDanceDownloadObject(url, knownLength))
            return;

        // Weak small responses may be junk; strong MIME / browser play still go through.
        // (YouTube ABR/range chunks report tiny Content-Length while the media itself is large).
        var ranged = IsRangedOrVolatileMediaUrl(url);
        if (!browserObserved && !ranged && !IsStrongMediaMime(mime) &&
            knownLength is > 0 and < MediaResourceSizeFilter.MinStrongMimeBytes &&
            !url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
            !url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            return;

        // Deduplicate by normalized URL so YouTube/TikTok range/ABR variants share one probe slot.
        var mediaKey = MediaUrlNormalizer.Normalize(url);
        if (ExtractContentIdFromUrl(url) is { } urlOwner)
            _owners[mediaKey] = "id:" + urlOwner;
        else if (MediaOwnership.ForPage(page, _observedIdentity) is { } pageOwner && !IsDouyinLiveStream(url))
            _owners[mediaKey] = pageOwner;
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
                    BrowserObservedMedia? evidence = null;
                    var trustBrowserObservation = observed &&
                        !IsManifestAddress(probeUrl, mime) &&
                        !IsInsufficientByteDanceDownloadObject(probeUrl, knownLength) &&
                        _browserObserved.TryGetValue(mediaKey, out evidence) &&
                        !IsInsufficientByteDanceDownloadObject(evidence.Url, evidence.ContentLength) &&
                        (knownLength is null ||
                         knownLength >= MediaResourceSizeFilter.MinStrongMimeBytes ||
                         ranged ||
                         IsRangedOrVolatileMediaUrl(probeUrl));
                    if (trustBrowserObservation && evidence is not null)
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
            evidence.ContentLength,
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
        if (manifestKind == "hls")
        {
            // HLS segments may use opaque or image-like paths. Classify their bytes,
            // while keeping playlist access restricted to network protocols.
            foreach (var arg in new[] { "-protocol_whitelist", "http,https,tcp,tls,crypto",
                         "-allowed_extensions", "ALL", "-allowed_segment_extensions", "ALL", "-extension_picky", "0" })
                psi.ArgumentList.Add(arg);
        }
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
        var probeError = await stderr;
        // A plausible URL is still only a candidate; a failed validation is never a valid result.
        if (process.ExitCode != 0)
        {
            _logger.LogInformation("Stream inspection failed host={Host} detail={Detail}", url.Host,
                probeError.Length > 1600 ? probeError[..1600] : probeError);
            return null;
        }

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

    private void EmitBuilt(IReadOnlyList<DetectedVideo> videos)
    {
        if (videos.Count == 0)
            return;

        foreach (var video in videos)
        {
            VideoDetected?.Invoke(this, video);
            VideoUpdated?.Invoke(this, video);
        }

        PageProbed?.Invoke(this, videos);
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
                                ?? (ExtractContentIdFromUrl(entry.Value.Track.SourceUrl) is { } fromUrl
                                    ? "id:" + fromUrl
                                    : null)
                                ?? (owner is not null &&
                                    owner.StartsWith("id:", StringComparison.Ordinal) &&
                                    !IsDouyinLiveStream(entry.Value.Track.SourceUrl)
                                    ? owner
                                    : null);
                return entry.Value with
                {
                    Track = entry.Value.Track with { ContentIdentity = inherited }
                };
            })
            .Where(m => page is null || SamePage(m.Page, page))
            .Where(m =>
            {
                if (owner is null) return true;
                var pageId = NormalizeContentId(owner);
                var trackId = NormalizeContentId(m.Track.ContentIdentity) ??
                              ExtractContentIdFromUrl(m.Track.SourceUrl);
                if (trackId is not null && pageId is not null)
                    return string.Equals(trackId, pageId, StringComparison.OrdinalIgnoreCase);
                // Orphan live previews must not pollute a concrete short-video identity.
                if (pageId is not null && IsDouyinLiveStream(m.Track.SourceUrl))
                    return false;
                return trackId is null;
            })
            .ToArray();

        // When the active item already has progressive VOD, drop leftover live HLS/FLV.
        if (probed.Any(p => !IsDouyinLiveStream(p.Track.SourceUrl) &&
                            p.Track.Kind is MediaTrackKind.Video or MediaTrackKind.Combined))
            probed = probed.Where(p => !IsDouyinLiveStream(p.Track.SourceUrl)).ToArray();

        // Drop Douyin/TikTok MSE Range windows that slipped through with a known tiny length.
        probed = probed
            .Where(p => !IsInsufficientByteDanceDownloadObject(p.Track.SourceUrl, p.Track.ContentLength))
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

        var recovery = page is null
            ? null
            : VideoDownloader.Infrastructure.Download.MediaAddressRenewal.RecoveryAddress(page, owner);

        // Stamp page/item ownership onto variants before split so V/A siblings share one card.
        if (owner is not null)
        {
            merged = merged with
            {
                Variants = merged.Variants.Select(v => v with
                {
                    ContentIdentity = v.ContentIdentity ?? owner,
                    Tracks = v.Tracks.Select(t => t with
                    {
                        ContentIdentity = t.ContentIdentity ?? owner
                    }).ToArray()
                }).ToArray()
            };
        }

        var built = SplitByMediaGroup(merged, page, owner, recovery);
        // Keep PreferDisplayTitle / UpdateCaption results — do not flatten back to document.title.

        _lastBuilt = built;

        // Defer UI emission until discovery completes so the list shows the final fact set once.
        if (!forceEmit && _session.Phase != DetectionPhase.Completed)
            return;

        EmitBuilt(built);
    }

    private DetectedVideo BuildAggregatedVideo(IReadOnlyList<Probed> all, string? extractedCaption)
    {
        var page = all[0].Page;
        var sourceName = Path.GetFileName(all.Select(a => a.Track.SourceUrl.AbsolutePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) &&
                                    !IsWeakCaption(Path.GetFileName(path))) ?? "");
        // Caption only. Prefer player/JSON caption, then external extract, then document.title.
        var title = _titleFromCaption && !IsWeakCaption(_title)
            ? _title!
            : !IsWeakCaption(extractedCaption)
                ? extractedCaption!
            : !IsWeakCaption(_title)
                ? _title!
            : IsDirectMediaPage(page) && !IsWeakCaption(sourceName)
                ? sourceName!
                : !IsWeakCaption(sourceName) ? sourceName! : "视频";

        var variants = all.Where(a => a.Manifest is not null).SelectMany(a => a.Manifest!.Variants)
            .Where(v => v.Tracks.Count > 1).ToList();
        var singleTracks = all.SelectMany(a => a.Manifest is null ? new[] { a } :
            a.Manifest.Variants.Where(v => v.Tracks.Count == 1).Select(v => new Probed(a.Page,
                v.Tracks[0] with { ContentIdentity = a.Track.ContentIdentity ?? v.Tracks[0].ContentIdentity }, 0, v.Height))).ToArray();
        var videos = singleTracks.Where(a => a.Track.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
            .Where(a => !IsInsufficientByteDanceDownloadObject(a.Track.SourceUrl, a.Track.ContentLength))
            .OrderBy(a => MediaVariantRanking.IsFlvLike(a.Track) ? 1 : 0)
            .ThenByDescending(a => a.Track.Kind == MediaTrackKind.Combined ? 1 : 0)
            .ThenByDescending(a => a.Height ?? 0)
            .ThenByDescending(a => a.Track.ContentLength ?? 0)
            .ToArray();
        var audios = singleTracks.Where(a => a.Track.Kind == MediaTrackKind.Audio)
            .Where(a => !IsInsufficientByteDanceDownloadObject(a.Track.SourceUrl, a.Track.ContentLength))
            .OrderBy(a => MediaVariantRanking.IsFlvLike(a.Track) ? 1 : 0)
            .ThenByDescending(a => a.Track.ContentLength ?? a.Track.Bandwidth ?? 0)
            // Progressive Combined can donate audio to a higher video-only sibling.
            // HLS/DASH Combined is already a full A+V playlist — never pair it as audio-extract.
            .Concat(singleTracks.Where(a => a.Track.Kind == MediaTrackKind.Combined)
                .Where(a => a.Track.Container is not ("hls" or "dash"))
                .Where(a => !IsInsufficientByteDanceDownloadObject(a.Track.SourceUrl, a.Track.ContentLength))
                .Select(a => a with { Track = a.Track with { Kind = MediaTrackKind.Audio, TrackId = "audio-extract", Codec = null } }))
            .ToArray();

        // Photo / album mode: equal-duration image slideshow + BGM.
        // Require 2+ stills, or a Douyin/TikTok note page with at least one still.
        var albumMin = page.AbsolutePath.Contains("/note/", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        if (_albumImages.Count >= albumMin)
        {
            var albumAudio = audios.FirstOrDefault()?.Track
                ?? singleTracks.Select(a => a.Track).FirstOrDefault(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined);
            if (albumAudio is not null)
            {
                var owner = MediaOwnership.ForPage(page, _observedIdentity);
                var imageTracks = _albumImages
                    .DistinctBy(u => MediaUrlNormalizer.Normalize(u))
                    .Take(48)
                    .Select((url, i) => new MediaTrack(
                        $"album-img-{i}",
                        MediaTrackKind.Image,
                        url,
                        null,
                        "image",
                        null,
                        null,
                        albumAudio.RequestContext)
                    {
                        IsValidated = true,
                        ContentIdentity = owner ?? albumAudio.ContentIdentity,
                        Evidence = MediaEvidence.DomObserved
                    })
                    .ToArray();
                if (imageTracks.Length > 0)
                {
                    var tracks = imageTracks.Append(albumAudio with
                    {
                        Kind = MediaTrackKind.Audio,
                        ContentIdentity = owner ?? albumAudio.ContentIdentity
                    }).ToArray();
                    variants.Add(MediaVariant.FromTracks(
                        $"相册视频（{imageTracks.Length} 张）",
                        null,
                        null,
                        null,
                        "album",
                        tracks) with
                    {
                        ContentIdentity = owner,
                        RecoveryPageUrl = page
                    });
                }
            }
        }

        foreach (var item in videos)
        {
            var bestAudio = audios
                .Where(audio => CanPair(item, audio) && SameMediaGroup(item.Track, audio.Track))
                .OrderBy(audio => MediaVariantRanking.IsFlvLike(audio.Track) ? 1 : 0)
                .ThenBy(audio => IsDouyinLiveStream(audio.Track.SourceUrl) ? 1 : 0)
                .ThenByDescending(audio => IsLikelyVodAudioUrl(audio.Track.SourceUrl) ? 1 : 0)
                .ThenByDescending(audio => audio.Track.ContentLength ?? audio.Track.Bandwidth ?? 0)
                .FirstOrDefault();
            var track = item.Track;
            if (track.Kind == MediaTrackKind.Combined)
            {
                variants.Add(MediaVariant.FromTracks(
                    FormatVideoLabel(item.Height),
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
                    FormatVideoLabel(item.Height),
                    null,
                    item.Height,
                    (track.Bandwidth ?? 0) + (bestAudio.Track.Bandwidth ?? 0),
                    "mkv",
                    [track, bestAudio.Track]));
            }
            else
            {
                // Douyin/TikTok: when no audio candidate survived discovery, still expose a
                // browser-proven progressive object as Combined so download is not blocked.
                var promoteCombined = item.Track.BrowserObserved &&
                    audios.Length == 0 &&
                    IsByteDanceOrTikTokMediaCdn(item.Track.SourceUrl) &&
                    !IsInsufficientByteDanceDownloadObject(item.Track.SourceUrl, item.Track.ContentLength);
                variants.Add(MediaVariant.FromTracks(
                    FormatVideoLabel(item.Height, promoteCombined ? null : (audios.Length > 0 ? "音轨待匹配" : "无音轨")),
                    null,
                    item.Height,
                    track.Bandwidth,
                    track.Container,
                    [promoteCombined ? track with { Kind = MediaTrackKind.Combined } : track]));
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

        // Deduplicate ABR history within the same media group only — never collapse distinct videos.
        variants = variants
            .GroupBy(v =>
            {
                var audioOnly = v.Tracks.Count > 0 && v.Tracks.All(t => t.Kind == MediaTrackKind.Audio);
                var group = VariantMediaGroupKey(v);
                return audioOnly
                    ? $"a|{group}|{v.Container}|{v.SourceUrl.AbsoluteUri}"
                    : $"v|{group}|{v.Height ?? 0}|{v.Container}|{v.VideoCodec}|{v.AudioCodec}|{string.Join(',',v.Tracks.Where(t=>t.Kind==MediaTrackKind.Audio).Select(t=>t.TrackId))}";
            }, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                    .ThenByDescending(v => v.Height ?? 0).ToArray();
                return ordered[0] with { Alternatives = ordered.Skip(1).Take(4).ToArray() };
            })
            .Where(v => !MediaResourceSizeFilter.ShouldExcludeVariant(v))
            .OrderByDescending(v => v.Tracks.Any(t => t.BrowserObserved))
            .ThenBy(v => v.Tracks.Any(t =>
                IsInsufficientByteDanceDownloadObject(t.SourceUrl, t.ContentLength)) ? 1 : 0)
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

    /// <summary>
    /// Split one page aggregate into one DetectedVideo per independent media group so the UI can
    /// select each video with matching video/audio dropdowns.
    /// </summary>
    private IReadOnlyList<DetectedVideo> SplitByMediaGroup(
        DetectedVideo merged,
        Uri? page,
        string? owner,
        Uri? recovery)
    {
        var videoVariants = merged.Variants
            .Where(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined or MediaTrackKind.Image))
            .ToArray();
        var audioOnlyVariants = merged.Variants
            .Where(v => v.Tracks.Count > 0 && v.Tracks.All(t => t.Kind == MediaTrackKind.Audio))
            .ToArray();

        // Prefer explicit item ContentIdentity (Douyin V/A siblings). Fall back to URL session so
        // multi-player pages without per-item ids keep each stream as its own card.
        var bags = videoVariants
            .GroupBy(VariantSplitKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MediaGroupBag(g.Key, g.ToList()))
            .ToList();

        foreach (var audio in audioOnlyVariants)
        {
            var home = bags.FirstOrDefault(b => AudioBelongsToGroup(audio, b.Variants))
                       ?? bags.FirstOrDefault(b => string.Equals(b.Key, VariantSplitKey(audio), StringComparison.OrdinalIgnoreCase));
            if (home is null)
            {
                home = new MediaGroupBag(VariantSplitKey(audio), []);
                bags.Add(home);
            }

            home.Variants.Add(audio);
        }

        if (bags.Count == 0)
            return [];

        var ordered = bags
            .OrderByDescending(b => b.Variants.Max(v => v.TotalContentLength ?? v.Bandwidth ?? 0))
            .ThenByDescending(b => b.Variants.Max(v => v.Height ?? 0))
            .ToArray();

        var results = new List<DetectedVideo>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            var bag = ordered[i];
            var groupKey = bag.Key;
            var groupVariants = bag.Variants.ToArray();
            var stamped = groupVariants.Select(v => v with
            {
                ContentIdentity = owner ?? v.ContentIdentity,
                RecoveryPageUrl = recovery ?? v.RecoveryPageUrl,
                // Keep same-group ABR alternatives only — never pull another video's tracks here.
                Alternatives = v.Alternatives
                    .Where(a => groupVariants.Any(g =>
                        string.Equals(
                            MediaUrlNormalizer.SessionKey(PrimaryMediaTrack(g).SourceUrl),
                            MediaUrlNormalizer.SessionKey(PrimaryMediaTrack(a).SourceUrl),
                            StringComparison.OrdinalIgnoreCase)))
                    .Concat(groupVariants.Where(a => a != v &&
                        VideoDownloader.Infrastructure.Download.MediaAddressRenewal.Compatible(v, a)))
                    .DistinctBy(a => string.Join('|', a.Tracks.Select(t => t.SourceUrl.AbsoluteUri)))
                    .Take(4)
                    .Select(a => a with
                    {
                        ContentIdentity = owner ?? a.ContentIdentity,
                        RecoveryPageUrl = recovery ?? a.RecoveryPageUrl,
                        Alternatives = []
                    })
                    .ToArray()
            }).ToArray();

            var pageUri = page ?? merged.PageUrl;
            var stable = "page:" + pageUri.AbsoluteUri + "|media:" + groupKey;
            var videoId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(stable)).AsSpan(0, 16));
            var title = ordered.Length > 1
                ? (IsWeakCaption(merged.DisplayTitle) ? $"视频 {i + 1}" : $"{merged.DisplayTitle} · {i + 1}")
                : merged.DisplayTitle;

            results.Add(merged with
            {
                VideoId = videoId,
                SessionId = _session.Id,
                SiteContentId = merged.SiteContentId ?? (ordered.Length > 1 ? $"part:{i + 1}" : null),
                DisplayTitle = title,
                Variants = stamped,
                Family = stamped.Any(v => v.Tracks.Any(t => t.Container == "hls")) ? MediaFamily.Hls
                    : stamped.Any(v => v.Tracks.Any(t => t.Container == "dash")) ? MediaFamily.Dash
                    : merged.Family
            });
        }

        return results;
    }

    private sealed class MediaGroupBag(string key, List<MediaVariant> variants)
    {
        public string Key { get; } = key;
        public List<MediaVariant> Variants { get; } = variants;
    }

    private static bool AudioBelongsToGroup(MediaVariant audio, IReadOnlyList<MediaVariant> groupVariants)
    {
        var audioTrack = audio.Tracks[0];
        var audioId = NormalizeContentId(audioTrack.ContentIdentity);
        var audioSession = MediaUrlNormalizer.SessionKey(audioTrack.SourceUrl);
        foreach (var variant in groupVariants)
        {
            foreach (var track in variant.Tracks)
            {
                var trackId = NormalizeContentId(track.ContentIdentity);
                if (audioId is not null && trackId is not null &&
                    string.Equals(audioId, trackId, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(audioSession, MediaUrlNormalizer.SessionKey(track.SourceUrl), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    internal static MediaTrack PrimaryMediaTrack(MediaVariant variant) =>
        variant.Tracks.FirstOrDefault(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined or MediaTrackKind.Image)
        ?? variant.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio)
        ?? variant.Tracks[0];

    /// <summary>Stable key for tests / diagnostics: item id when present, else video session URL.</summary>
    internal static string VariantMediaGroupKey(MediaVariant variant) => VariantSplitKey(variant);

    internal static string VariantSplitKey(MediaVariant variant)
    {
        var variantId = NormalizeContentId(variant.ContentIdentity);
        if (variantId is not null)
            return "id:" + variantId;

        foreach (var track in variant.Tracks)
        {
            var id = NormalizeContentId(track.ContentIdentity);
            if (id is not null)
                return "id:" + id;
        }

        var primary = PrimaryMediaTrack(variant);
        return "url:" + MediaUrlNormalizer.SessionKey(primary.SourceUrl);
    }

    internal static string MediaGroupKey(MediaTrack track)
    {
        var id = NormalizeContentId(track.ContentIdentity);
        if (id is not null)
            return "id:" + id;
        return "url:" + MediaUrlNormalizer.SessionKey(track.SourceUrl);
    }

    /// <summary>
    /// Pairing helper: same content id (Douyin V/A) or same URL session (extract from same Combined).
    /// </summary>
    internal static bool SameMediaGroup(MediaTrack left, MediaTrack right)
    {
        var leftId = NormalizeContentId(left.ContentIdentity);
        var rightId = NormalizeContentId(right.ContentIdentity);
        if (leftId is not null && rightId is not null &&
            string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(
            MediaUrlNormalizer.SessionKey(left.SourceUrl),
            MediaUrlNormalizer.SessionKey(right.SourceUrl),
            StringComparison.OrdinalIgnoreCase);
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
        if (!IsWeakCaption(local)) return local;
        if (!IsWeakCaption(external)) return external;
        return string.IsNullOrWhiteSpace(local) ? external : local;
    }

    /// <summary>
    /// Empty, placeholder, host-looking, or transport/segment filenames — not a usable caption.
    /// Generic pages may fall back to document.title instead.
    /// </summary>
    internal static bool IsWeakCaption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value is "视频") return true;
        if (value.Contains('.', StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal) &&
            (value.EndsWith(".com", StringComparison.OrdinalIgnoreCase) ||
             value.EndsWith(".tv", StringComparison.OrdinalIgnoreCase) ||
             value.EndsWith(".cc", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (value.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".flv", StringComparison.OrdinalIgnoreCase))
            return true;
        // Bare media filenames without spaces (0626_1.m4s already covered; also foo.mp4).
        if (!value.Contains(' ', StringComparison.Ordinal) &&
            Path.HasExtension(value) &&
            value.Length <= 64 &&
            Regex.IsMatch(value, @"^[\w.-]+\.(mp4|webm|m4a|mp3|aac|mkv)$", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    private static bool CanPair(Probed video, Probed audio)
    {
        if (!SamePage(video.Page, audio.Page))
            return false;

        // Live HLS/FLV must never mux with progressive short-video objects.
        if (IsDouyinLiveStream(video.Track.SourceUrl) != IsDouyinLiveStream(audio.Track.SourceUrl))
            return false;

        var videoId = NormalizeContentId(video.Track.ContentIdentity) ??
                      ExtractContentIdFromUrl(video.Track.SourceUrl);
        var audioId = NormalizeContentId(audio.Track.ContentIdentity) ??
                      ExtractContentIdFromUrl(audio.Track.SourceUrl);
        if (videoId is not null && audioId is not null)
            return string.Equals(videoId, audioId, StringComparison.OrdinalIgnoreCase);

        // Prefer real VOD audio tracks (media-audio / ies-music) over orphans.
        if (!IsLikelyVodAudioUrl(audio.Track.SourceUrl) &&
            !IsDouyinLiveStream(audio.Track.SourceUrl) &&
            audio.Track.Kind != MediaTrackKind.Audio)
            return false;

        // Full HLS/DASH playlists are not audio donors for a separate video object.
        if (audio.Track.TrackId == "audio-extract" &&
            audio.Track.Container is "hls" or "dash")
            return false;

        // Orphan tracks on a shared feed host must not invent a pair.
        if (videoId is null && audioId is null)
            return IsLikelyVodAudioUrl(audio.Track.SourceUrl) &&
                   !IsDouyinLiveStream(video.Track.SourceUrl);

        // One-sided stamp: require the stamped id to match page ownership when the URL
        // itself encodes a content id; otherwise trust SamePage (active feed item).
        var stamped = videoId ?? audioId!;
        var pageOwner = NormalizeContentId(MediaOwnership.ForPage(video.Page, null));
        if (pageOwner is not null)
            return string.Equals(stamped, pageOwner, StringComparison.OrdinalIgnoreCase);

        return IsLikelyVodAudioUrl(audio.Track.SourceUrl);
    }

    private static bool IsLikelyVodAudioUrl(Uri url)
    {
        var path = url.AbsolutePath;
        return path.Contains("/media-audio-", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/ies-music/", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collapse <c>id:123</c>, <c>content:douyin:123</c>, <c>host:content:123</c> to bare <c>123</c>.
    /// </summary>
    internal static string? NormalizeContentId(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return null;
        if (identity.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            return identity[3..].Trim();
        var match = Regex.Match(identity, @"content:(?:[A-Za-z][\w-]*:)?([A-Za-z0-9_-]{6,})",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : identity.Trim();
    }

    private static bool IsLikelyCrossSiteLeak(Uri page, Uri media)
    {
        var pageHost = page.Host;
        var mediaHost = media.Host;
        var pageBili = pageHost.Contains("bilibili", StringComparison.OrdinalIgnoreCase);
        var mediaBili = mediaHost.Contains("bilivideo", StringComparison.OrdinalIgnoreCase) ||
                        mediaHost.Contains("bilibili", StringComparison.OrdinalIgnoreCase) ||
                        (mediaHost.Contains("akamaized", StringComparison.OrdinalIgnoreCase) &&
                         media.AbsolutePath.Contains("upos", StringComparison.OrdinalIgnoreCase));
        if (mediaBili && !pageBili)
            return true;

        var pageDy = pageHost.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
                     pageHost.Contains("iesdouyin", StringComparison.OrdinalIgnoreCase);
        var mediaDy = mediaHost.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
                      mediaHost.Contains("byteicdn", StringComparison.OrdinalIgnoreCase) ||
                      mediaHost.Contains("zjcdn", StringComparison.OrdinalIgnoreCase) ||
                      mediaHost.Contains("douyinvod", StringComparison.OrdinalIgnoreCase) ||
                      mediaHost.Contains("douyincdn", StringComparison.OrdinalIgnoreCase);
        if (mediaDy && !pageDy && !pageHost.Contains("tiktok", StringComparison.OrdinalIgnoreCase))
            return true;

        var pageTt = pageHost.Contains("tiktok", StringComparison.OrdinalIgnoreCase);
        var mediaTt = mediaHost.Contains("tiktok", StringComparison.OrdinalIgnoreCase);
        if (mediaTt && !pageTt && !pageDy)
            return true;

        return false;
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

    /// <summary>User-facing variant title. Never invent "默认" when height is unknown.</summary>
    private static string FormatVideoLabel(int? height, string? note = null)
    {
        var label = height is > 0 ? $"视频 {height}p" : "视频";
        return string.IsNullOrWhiteSpace(note) ? label : $"{label}（{note}）";
    }

    private static string FormatAudioLabel(Probed audio)
    {
        if (audio.Track.ContentLength is > 0)
            return FormatBytes(audio.Track.ContentLength.Value);
        if (audio.Track.Bandwidth is > 0)
            return $"{audio.Track.Bandwidth / 1000}kbps";
        if (audio.Duration > 0)
            return $"{audio.Duration:F0}s";
        return "音轨";
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
            .Where(o => !IsManifestAddress(o.Url, o.Mime))
            .Where(o => !IsDouyinLiveStream(o.Url))
            .Where(o => !IsInsufficientByteDanceDownloadObject(o.Url, o.ContentLength))
            .Where(o => o.KindHint is MediaTrackKind.Video or MediaTrackKind.Combined)
            .GroupBy(o => MediaUrlNormalizer.Normalize(o.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(o => o.ContentLength ?? 0).First())
            .OrderByDescending(o => o.KindHint == MediaTrackKind.Combined ? 1 : 0)
            .ThenByDescending(o => o.ContentLength ?? 0)
            .ToArray();
        if (browserVideos.Length == 0)
            return video;

        var existing = video.Variants
            .SelectMany(v => v.Tracks)
            .Select(t => MediaUrlNormalizer.Normalize(t.SourceUrl))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var owner = MediaOwnership.ForPage(video.PageUrl, video.SiteContentId is null ? null : "content:" + video.SiteContentId)
                    ?? (video.SiteContentId is null ? null : "id:" + video.SiteContentId);
        var pairedAudio = video.Variants
            .SelectMany(v => v.Tracks)
            .Where(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined)
            .Where(t => !MediaVariantRanking.IsFlvLike(t))
            .Where(t => !IsDouyinLiveStream(t.SourceUrl))
            .Where(t => !IsInsufficientByteDanceDownloadObject(t.SourceUrl, t.ContentLength))
            .Where(t => IsLikelyVodAudioUrl(t.SourceUrl) || t.Kind == MediaTrackKind.Audio)
            .OrderByDescending(t => IsLikelyVodAudioUrl(t.SourceUrl) ? 1 : 0)
            .ThenByDescending(t => t.ContentLength ?? t.Bandwidth ?? 0)
            .FirstOrDefault();
        if (pairedAudio is { Kind: MediaTrackKind.Combined })
            pairedAudio = pairedAudio with { Kind = MediaTrackKind.Audio, TrackId = "audio-extract", Codec = null };

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
            var kind = observed.KindHint == MediaTrackKind.Audio
                ? MediaTrackKind.Video
                : observed.KindHint;
            // Muxed progressive needs no invented audio partner.
            var needsAudio = kind == MediaTrackKind.Video && pairedAudio is not null;
            var track = new MediaTrack(
                $"browser-video-{index++}",
                needsAudio ? MediaTrackKind.Video : MediaTrackKind.Combined,
                observed.Url,
                null,
                container,
                null,
                observed.ContentLength,
                observed.Context)
            {
                IsValidated = true,
                BrowserObserved = true,
                Evidence = MediaEvidence.BrowserObserved,
                ContentIdentity = owner ?? (ExtractContentIdFromUrl(observed.Url) is { } vid ? "id:" + vid : null)
            };
            var tracks = needsAudio
                ? new[] { track, pairedAudio! with { ContentIdentity = owner ?? pairedAudio!.ContentIdentity } }
                : new[] { track };
            injected.Add(MediaVariant.FromTracks(
                needsAudio ? "视频 (浏览器实播+音轨)" : "视频 (浏览器实播)",
                null,
                null,
                null,
                needsAudio ? "mkv" : container,
                tracks) with
            {
                ContentIdentity = owner,
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
        RequestContext Context,
        long? ContentLength = null);

    internal sealed record Probed(Uri Page, MediaTrack Track, double Duration, int? Height, ManifestResolutionResult? Manifest = null);
}
