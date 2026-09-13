using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Logging;

namespace VideoDownloader.Infrastructure.Browser;

public sealed class WebView2Host : IAsyncDisposable, IDisposable
{
    private readonly AppOptions _options;
    private readonly INetworkEventNormalizer _normalizer;
    private readonly IMediaDetectionPipeline _pipeline;
    private readonly ILogger<WebView2Host> _logger;
    private readonly Channel<(RawNetworkEvent Event, IDiscoveryScope Scope)> _events;
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new();
    private readonly Dictionary<string, string> _currentHeaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _snapshotLock = new();

    private WebView2? _webView;
    private Dispatcher? _uiDispatcher;
    private CoreWebView2? _core;
    private CoreWebView2DevToolsProtocolEventReceiver? _cdpRequestReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _cdpResponseReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _cdpFinishedReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _cdpFailedReceiver;
    private readonly ConcurrentDictionary<string, (RawNetworkEvent Event, IDiscoveryScope Scope)> _responseBodies = new();
    private Task? _consumerTask;
    private CancellationTokenSource? _consumerCts;
    private CoreWebView2DevToolsProtocolEventReceiver? _cdpAttachedReceiver;
    private bool _cdpEnabled;
    private Uri? _lastDocumentUrl;
    private string? _lastPageNotificationKey;
    private ContextSnapshot _snapshot = ContextSnapshot.Empty;
    private long _navigationGeneration;
    private bool _captureEnabled = true;
    private string? _mediaSessionKey;
    private string? _pendingMediaSessionKey;
    private double? _mediaDurationSec;
    private CancellationTokenSource? _mediaSessionDebounceCts;
    private readonly object _mediaSessionLock = new();

    public WebView2Host(
        IOptions<AppOptions> options,
        INetworkEventNormalizer normalizer,
        IMediaDetectionPipeline pipeline,
        ILogger<WebView2Host> logger)
    {
        _options = options.Value;
        _normalizer = normalizer;
        _pipeline = pipeline;
        _logger = logger;
        _events = Channel.CreateBounded<(RawNetworkEvent Event, IDiscoveryScope Scope)>(new BoundedChannelOptions(_options.Detection.ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public Uri? CurrentPageUrl => _lastDocumentUrl ?? (_core?.Source is not null ? new Uri(_core.Source) : null);

    public bool CaptureEnabled
    {
        get => _captureEnabled;
        set => _captureEnabled = value;
    }

    public long NavigationGeneration => Interlocked.Read(ref _navigationGeneration);

    public event EventHandler<string>? NavigationStarted;
    public event EventHandler<PageIdentityChangedEventArgs>? PageIdentityChanged;
    public event EventHandler<string>? OpenInNewTabRequested;
    public event EventHandler<MediaSessionChangedEventArgs>? MediaSessionChanged;
    public event EventHandler<bool>? LocalPlayerFullscreenRequested;

    public string? CurrentMediaSessionKey
    {
        get
        {
            lock (_mediaSessionLock)
                return _mediaSessionKey;
        }
    }

    public Task NotifyLocalPlayerFullscreenAsync(bool active)
    {
        if (_core is null)
            return Task.CompletedTask;
        var flag = active ? "true" : "false";
        var script =
            "try{window.__vdSetHostFullscreen&&window.__vdSetHostFullscreen(" + flag + ");}catch(e){}";
        return _core.ExecuteScriptAsync(script);
    }

    public async Task InitializeAsync(WebView2 webView, CancellationToken ct = default)
    {
        _webView = webView;
        _uiDispatcher = webView.Dispatcher;
        var userData = PathExpander.Expand(_options.Browser.UserDataFolder);
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await webView.EnsureCoreWebView2Async(env);

        _core = webView.CoreWebView2;
        _core.Settings.AreDevToolsEnabled = true;

        _consumerCts = new CancellationTokenSource();
        _consumerTask = ConsumeAsync(_consumerCts.Token);

        await EnableCdpNetworkAsync(ct);
        await InstallMediaSessionWatchAsync(ct);

        _core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var url))
            {
                Interlocked.Increment(ref _navigationGeneration);
                _lastDocumentUrl = url;
                ResetMediaSessionAnchor();
                ClearPendingRequests();
                if (_normalizer is VideoDownloader.Core.Detection.NetworkEventNormalizer normalizer) normalizer.Clear();
                NavigationStarted?.Invoke(this, e.Uri);
                NotifyPageIdentityChanged(url, null);
            }
        };

        _core.SourceChanged += (_, _) =>
        {
            if (_core.Source is not null && Uri.TryCreate(_core.Source, UriKind.Absolute, out var url))
            {
                _lastDocumentUrl = url;
                NotifyPageIdentityChanged(url, _core.DocumentTitle);
            }

            _ = NotifyCurrentDocumentIdentityAsync();
        };

        _core.HistoryChanged += (_, _) =>
        {
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            _ = NotifyCurrentDocumentIdentityAsync();
        };
        _core.DocumentTitleChanged += (_, _) => _ = NotifyCurrentDocumentIdentityAsync();

        _core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(e.Uri))
                OpenInNewTabRequested?.Invoke(this, e.Uri);
        };

        _core.NavigationCompleted += async (_, e) =>
        {
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                if (!e.IsSuccess || !_captureEnabled)
                    return;

                await NotifyCurrentDocumentIdentityAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await RefreshContextSnapshotAsync(cts.Token);
                // Do not probe here — MainViewModel owns the full detection cycle on NavigationStarted / session change.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Context snapshot refresh failed after navigation.");
            }
        };

        await RefreshContextSnapshotAsync(ct);
    }

    /// <summary>
    /// Clears network dedup so the next full detection cycle can re-accept media events.
    /// </summary>
    public void ResetDetectionSession()
    {
        foreach (var key in _responseBodies.Keys)
            if (_responseBodies.TryRemove(key, out var body)) body.Scope.Dispose();
        ClearPendingRequests();
        if (_normalizer is VideoDownloader.Core.Detection.NetworkEventNormalizer normalizer)
            normalizer.Clear();
    }

    private async Task InstallMediaSessionWatchAsync(CancellationToken ct)
    {
        if (_core is null)
            return;


        await _core.AddScriptToExecuteOnDocumentCreatedAsync(SiteObservationBootstrap.Install);
        _core.WebMessageReceived += OnWebMessageReceived;
        try
        {
            await _core.ExecuteScriptAsync(SiteObservationBootstrap.Install);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Initial media session watch inject failed.");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(json))
                return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var innerText = root.GetString();
                if (string.IsNullOrWhiteSpace(innerText))
                    return;
                using var inner = JsonDocument.Parse(innerText);
                TryHandleMediaMessage(inner.RootElement);
                return;
            }

            TryHandleMediaMessage(root);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebMessage media session parse failed.");
        }
    }

    private void TryHandleMediaMessage(JsonElement root)
    {
        if (root.TryGetProperty("type", out var fsType) &&
            fsType.GetString() == "vd-local-fullscreen")
        {
            var active = root.TryGetProperty("active", out var activeEl) &&
                         activeEl.ValueKind is JsonValueKind.True;
            LocalPlayerFullscreenRequested?.Invoke(this, active);
            return;
        }

        if (root.TryGetProperty("type", out var messageType) && messageType.GetString() == "vd-video-identity")
        {
            if (_captureEnabled && root.TryGetProperty("identity", out var identity) &&
                root.TryGetProperty("href", out var href) && href.GetString() == CurrentPageUrl?.AbsoluteUri &&
                identity.GetString() is { Length: > 0 } key)
            {
                double? durationSec = null;
                if (root.TryGetProperty("durationSec", out var durationEl) &&
                    durationEl.ValueKind == JsonValueKind.Number &&
                    durationEl.TryGetDouble(out var duration) &&
                    double.IsFinite(duration) &&
                    duration > 0)
                    durationSec = duration;

                ObserveVideoIdentity(key, CurrentPageUrl!, durationSec);
                if (root.TryGetProperty("caption", out var caption) &&
                    caption.GetString() is { Length: > 0 } text)
                {
                    bool apply;
                    lock (_mediaSessionLock)
                    {
                        var normalized = NormalizeMediaSessionKey(key);
                        apply = string.Equals(NormalizeMediaSessionKey(_mediaSessionKey ?? string.Empty), normalized, StringComparison.Ordinal) ||
                                string.Equals(NormalizeMediaSessionKey(_pendingMediaSessionKey ?? string.Empty), normalized, StringComparison.Ordinal);
                    }
                    if (apply)
                        _pipeline.UpdateCaption(_pipeline.SessionId, text);
                }
            }
            return;
        }
        if (!root.TryGetProperty("type", out var typeEl) ||
            typeEl.GetString() is not "vd-media-src")
            return;

        if (!root.TryGetProperty("src", out var srcEl))
            return;

        var src = srcEl.GetString();
        if (string.IsNullOrWhiteSpace(src) || !Uri.TryCreate(src, UriKind.Absolute, out var mediaUrl))
            return;

        var reason = root.TryGetProperty("reason", out var reasonElement) &&
                     reasonElement.ValueKind == JsonValueKind.String
            ? reasonElement.GetString()
            : null;
        ObserveMediaCandidate(mediaUrl,
            string.Equals(reason, "perf", StringComparison.OrdinalIgnoreCase) ? "dom-performance" : "dom-player");
    }

    public void ObserveMediaCandidate(Uri mediaUrl, string source, string? mime = null, long? contentLength = null)
    {
        // Kept for callers that report transport observations; these cannot change video identity.
    }

    internal void ObserveVideoIdentity(string key, Uri page, double? durationSec = null)
    {
        _lastDocumentUrl = page;
        var normalized = NormalizeMediaSessionKey(key);
        CancellationTokenSource debounce;
        bool durationRevision;
        lock (_mediaSessionLock)
        {
            durationRevision = IsSignificantDurationChange(_mediaDurationSec, durationSec) &&
                               string.Equals(NormalizeMediaSessionKey(_mediaSessionKey ?? string.Empty), normalized, StringComparison.Ordinal);
            if (durationSec is > 0)
                _mediaDurationSec = durationSec;

            if (durationRevision)
            {
                // Same content id but reliable duration jump ⇒ treat as a new work; force restart.
                _mediaSessionDebounceCts?.Cancel();
                _mediaSessionDebounceCts?.Dispose();
                _pendingMediaSessionKey = null;
                debounce = _mediaSessionDebounceCts = new();
                _ = CommitMediaSessionAsync(normalized, key, "duration-revision", debounce.Token, forceRestart: true);
                return;
            }

            if (normalized == NormalizeMediaSessionKey(_mediaSessionKey ?? string.Empty) &&
                _mediaSessionKey is not null)
            {
                _mediaSessionDebounceCts?.Cancel();
                _pendingMediaSessionKey = null;
                return;
            }
            if (normalized == NormalizeMediaSessionKey(_pendingMediaSessionKey ?? string.Empty) &&
                _pendingMediaSessionKey is not null)
                return;
            _pendingMediaSessionKey = normalized;
            _mediaSessionDebounceCts?.Cancel();
            _mediaSessionDebounceCts?.Dispose();
            debounce = _mediaSessionDebounceCts = new();
        }
        _ = CommitMediaSessionAsync(normalized, key, "dom-identity", debounce.Token);
    }

    private async Task CommitMediaSessionAsync(
        string key,
        string rawMediaUrl,
        string source,
        CancellationToken token,
        bool forceRestart = false)
    {
        try
        {
            // Settle ABR / multi-CDN bursts before committing a session switch.
            // Duration revisions are already debounced by the player; keep a short settle.
            await Task.Delay(forceRestart ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromSeconds(1.5), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        string? previous;
        lock (_mediaSessionLock)
        {
            if (token.IsCancellationRequested) return;
            var normalized = NormalizeMediaSessionKey(key);
            if (!forceRestart &&
                string.Equals(NormalizeMediaSessionKey(_mediaSessionKey ?? string.Empty), normalized, StringComparison.Ordinal) &&
                _mediaSessionKey is not null)
                return;

            previous = _mediaSessionKey;
            _mediaSessionKey = normalized;
            _pendingMediaSessionKey = null;
            key = normalized;
        }

        var page = CurrentPageUrl;
        if (page is null)
            return;

        // First primary media after navigation only anchors; avoids canceling the initial probe cycle.
        // Subsequent different session keys (same-URL feed swipe) restart detection.
        if (!forceRestart && previous is null && !_pipeline.IsCompleted)
            return;

        // Same aweme with different identity string prefixes must not restart detection.
        if (!forceRestart &&
            previous is not null &&
            string.Equals(NormalizeMediaSessionKey(previous), key, StringComparison.Ordinal))
            return;

        MediaSessionChanged?.Invoke(
            this,
            new MediaSessionChangedEventArgs(
                page,
                _core?.DocumentTitle,
                rawMediaUrl,
                previous ?? "unobserved",
                source,
                ForceRestart: forceRestart));
    }

    /// <summary>
    /// Collapse host-prefixed site ids to a stable content key.
    /// Digit aweme/item ids (Douyin/TikTok) unchanged; YouTube/Bilibili are additive.
    /// </summary>
    internal static string NormalizeMediaSessionKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return key;
        var digits = System.Text.RegularExpressions.Regex.Match(key, @"(\d{10,})");
        if (digits.Success)
            return "content:" + digits.Groups[1].Value;

        var yt = System.Text.RegularExpressions.Regex.Match(
            key,
            @"(?:content:)?youtube:(?<id>[\w-]{6,})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (yt.Success)
            return "content:youtube:" + yt.Groups["id"].Value;

        var bv = System.Text.RegularExpressions.Regex.Match(
            key,
            @"\b(?<id>BV[\w]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bv.Success)
            return "content:bilibili:" + bv.Groups["id"].Value;

        return key.Trim();
    }

    /// <summary>
    /// True when both durations are known and differ enough to indicate a different work
    /// (not merely NaN→metadata or tiny MSE timeline jitter).
    /// </summary>
    internal static bool IsSignificantDurationChange(double? previous, double? next)
    {
        if (previous is null or <= 0 || next is null or <= 0)
            return false;
        if (!double.IsFinite(previous.Value) || !double.IsFinite(next.Value))
            return false;
        return Math.Abs(previous.Value - next.Value) >= 2.0;
    }

    private void ResetMediaSessionAnchor()
    {
        lock (_mediaSessionLock)
        {
            _mediaSessionDebounceCts?.Cancel();
            _mediaSessionDebounceCts?.Dispose();
            _mediaSessionDebounceCts = null;
            _mediaSessionKey = null;
            _pendingMediaSessionKey = null;
            _mediaDurationSec = null;
        }
    }

    public Task NavigateAsync(string url, CancellationToken ct = default)
    {
        if (_core is null)
            throw new InvalidOperationException("WebView2 is not initialized.");

        if (_uiDispatcher is null || _uiDispatcher.CheckAccess())
        {
            _core.Navigate(url);
            return Task.CompletedTask;
        }

        return _uiDispatcher.InvokeAsync(() => _core.Navigate(url)).Task;
    }

    public bool CanGoBack => _core?.CanGoBack == true;
    public bool CanGoForward => _core?.CanGoForward == true;

    public event EventHandler? NavigationStateChanged;

    public void GoBack()
    {
        if (_core?.CanGoBack == true)
            _core.GoBack();
    }

    public void GoForward()
    {
        if (_core?.CanGoForward == true)
            _core.GoForward();
    }

    public Task ReloadAsync(CancellationToken ct = default)
    {
        _core?.Reload();
        return Task.CompletedTask;
    }

    public Dictionary<string, string> GetCurrentRequestHeaders() =>
        new(_currentHeaders, StringComparer.OrdinalIgnoreCase);

    public async Task ProbeCurrentPageAsync(CancellationToken ct = default)
    {
        if (_core is null || CurrentPageUrl is null)
            return;

        var session = _pipeline.SessionId;
        Diagnostics.HangProbe.Mark("host.probe.begin", $"session={session:N} page={CurrentPageUrl.AbsoluteUri}");
        var scriptJson = await ExecuteProbeScriptAsync(ct);
        Diagnostics.HangProbe.Mark("host.probe.afterScript", $"session={session:N} jsonLen={scriptJson?.Length ?? 0}");
        ct.ThrowIfCancellationRequested();
        if (session != _pipeline.SessionId || !_captureEnabled) return;
        var pageUrl = ExtractPageUrl(scriptJson) ?? CurrentPageUrl;
        if (pageUrl is null)
            return;

        _lastDocumentUrl = pageUrl;
        var title = ExtractPageTitle(scriptJson) ?? _core.DocumentTitle;
        // Do NOT NotifyPageIdentityChanged here — probe href/title jitter was restarting
        // the full detection cycle (Clear) and canceling ffprobe before results appeared.
        var context = CaptureCurrentContext(pageUrl, pageUrl);
        Diagnostics.HangProbe.Mark("host.probe.beforePipeline", pageUrl.AbsoluteUri);
        await _pipeline.ProbePageAsync(pageUrl, title, scriptJson, context, ct, runExternal: false);
        Diagnostics.HangProbe.Mark("host.probe.end", $"session={_pipeline.SessionId:N}");
    }

    private async Task<string?> ExecuteProbeScriptAsync(CancellationToken ct)
    {
        if (_core is null || _uiDispatcher is null)
            return null;

        async Task<string?> RunAsync()
        {
            var session = _pipeline.SessionId;
            Diagnostics.HangProbe.Mark("host.script.RunAsync", $"session={session:N} onUi={_uiDispatcher.CheckAccess()}");
            var exclusivePage = CurrentPageUrl is not null && IsExclusiveMediaHost(CurrentPageUrl);
            // REDUNDANT(pending-delete after confirm): always splice MediaAddressDiscoveryScript for exclusive pages.
            // var script = """...""".Replace("ADDRESS_DISCOVERY", MediaAddressDiscoveryScript.Expression);
            var script = exclusivePage
                ? """
                (() => {
                  const observation = window.__vdProbe?.() ?? window.__vdObserve?.();
                  const result = observation ?? {href:location.href,media:[]};
                  result.candidates = [...(result.candidates ?? [])];
                  return JSON.stringify(result);
                })();
                """
                : """
                (() => {
                  const observation = window.__vdProbe?.() ?? window.__vdObserve?.();
                  const result = observation ?? {href:location.href,media:[]};
                  const discovered = ADDRESS_DISCOVERY;
                  result.candidates = [...(result.candidates ?? []), ...discovered];
                  return JSON.stringify(result);
                })();
                """.Replace("ADDRESS_DISCOVERY", MediaAddressDiscoveryScript.Expression);
            ct.ThrowIfCancellationRequested();
            Diagnostics.HangProbe.Mark("host.script.ExecuteScript.begin", $"session={session:N}");
            var json = DecodeScriptResult(await _core.ExecuteScriptAsync(script).WaitAsync(ct));
            Diagnostics.HangProbe.Mark("host.script.ExecuteScript.end", $"session={session:N} len={json?.Length ?? 0}");
            if (session != _pipeline.SessionId || string.IsNullOrWhiteSpace(json)) return null;
            var payload = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            // Always harvest cross-frame player addresses (MacCMS / iframe HLS). Do not
            // gate on missing identity — pages may have a document title without media.
            // REDUNDANT(pending-delete after confirm): FrameAddressDiscovery on exclusive hosts (Douyin/YT/…).
            if (!exclusivePage)
            {
            try
            {
                Diagnostics.HangProbe.Mark("host.frameDiscovery.begin", $"session={session:N}");
                var addresses = await FrameAddressDiscovery.CollectAsync(_core.CallDevToolsProtocolMethodAsync,
                    () => session == _pipeline.SessionId && _captureEnabled, ct);
                Diagnostics.HangProbe.Mark("host.frameDiscovery.end", $"session={session:N} count={addresses.Count}");
                if (addresses.Count > 0)
                {
                    if (payload["candidates"] is not System.Text.Json.Nodes.JsonArray)
                        payload["candidates"] = new System.Text.Json.Nodes.JsonArray();
                    var candidates = payload["candidates"]!.AsArray();
                    var existing = candidates
                        .Select(c => c?["url"]?.GetValue<string>())
                        .Where(u => !string.IsNullOrWhiteSpace(u))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var address in addresses)
                    {
                        if (existing.Add(address))
                            candidates.Add(new System.Text.Json.Nodes.JsonObject { ["url"] = address });
                    }
                    _logger.LogInformation("Address discovery session={Session} frameCandidates={Count}", session, addresses.Count);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Diagnostics.HangProbe.Mark("host.frameDiscovery.timeout", $"session={session:N}");
                _logger.LogInformation("Frame discovery timed out session={Session}", session);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.HangProbe.Mark("host.frameDiscovery.fail", ex.GetType().Name);
                _logger.LogInformation("Frame discovery unavailable session={Session} reason={Reason}", session, ex.GetType().Name);
            }
            }
            else
            {
                Diagnostics.HangProbe.Mark("host.frameDiscovery.skipExclusive", $"session={session:N}");
            }
            return session == _pipeline.SessionId ? payload.ToJsonString() : null;
        }

        if (!_uiDispatcher.CheckAccess())
        {
            Diagnostics.HangProbe.Mark("host.script.InvokeAsync.begin");
            var result = await _uiDispatcher.InvokeAsync(RunAsync, DispatcherPriority.Background).Task.Unwrap();
            Diagnostics.HangProbe.Mark("host.script.InvokeAsync.end");
            return result;
        }

        return await RunAsync();
    }

    public async Task RefreshContextSnapshotAsync(CancellationToken ct = default, bool forceCookies = false)
    {
        if (_core is null || _uiDispatcher is null)
            return;

        if (!_uiDispatcher.CheckAccess())
        {
            await _uiDispatcher.InvokeAsync(
                () => RefreshContextSnapshotCoreAsync(ct, forceCookies),
                DispatcherPriority.Background).Task.Unwrap();
            return;
        }

        await RefreshContextSnapshotCoreAsync(ct, forceCookies);
    }

    public RequestContext CaptureCurrentContext(Uri? pageUrl, Uri resourceUrl)
    {
        ContextSnapshot snap;
        lock (_snapshotLock)
            snap = _snapshot;

        var headers = new Dictionary<string, string>(snap.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _currentHeaders)
            headers[key] = value;

        headers.TryGetValue("Referer", out var referer);
        headers.TryGetValue("Origin", out var origin);
        headers.TryGetValue("User-Agent", out var userAgent);

        return new RequestContext(
            Guid.NewGuid(),
            1,
            referer ?? pageUrl?.AbsoluteUri ?? snap.Referer,
            origin ?? snap.Origin,
            userAgent ?? snap.UserAgent,
            headers,
            snap.Cookies,
            DateTimeOffset.UtcNow);
    }

    private async Task RefreshContextSnapshotCoreAsync(CancellationToken ct, bool forceCookies = false)
    {
        if (_core is null)
            return;

        await RefreshCurrentHeadersAsync();

        _currentHeaders["User-Agent"] = _core.Settings.UserAgent;
        if (_core.Source is not null)
            _currentHeaders["Referer"] = _core.Source;

        IReadOnlyList<BrowserCookie> cookies = Array.Empty<BrowserCookie>();
        var pageUrl = CurrentPageUrl;
        // forceCookies: one-shot jar read for BrowserObserved CDN downloads without flipping the setting.
        if ((_options.Browser.CaptureCookies || forceCookies) && pageUrl is not null)
        {
            var cookieUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pageUrl.AbsoluteUri };
            foreach (var related in RelatedCookieUrls(pageUrl))
                cookieUrls.Add(related);

            var merged = new Dictionary<string, BrowserCookie>(StringComparer.OrdinalIgnoreCase);
            foreach (var cookieUrl in cookieUrls)
            {
                IReadOnlyList<CoreWebView2Cookie> cookieList;
                try
                {
                    cookieList = await _core.CookieManager.GetCookiesAsync(cookieUrl);
                }
                catch
                {
                    continue;
                }

                foreach (var c in cookieList)
                {
                    var mapped = new BrowserCookie(
                        c.Name,
                        c.Value,
                        c.Domain,
                        c.Path,
                        ToCookieExpiry(c.Expires),
                        c.IsSecure,
                        c.IsHttpOnly);
                    // Name+domain+path identity — later hosts may refresh values.
                    merged[$"{mapped.Domain}|{mapped.Path}|{mapped.Name}"] = mapped;
                }
            }

            cookies = merged.Values.ToList();
        }

        lock (_snapshotLock)
        {
            _snapshot = new ContextSnapshot(
                _currentHeaders.GetValueOrDefault("User-Agent"),
                _currentHeaders.GetValueOrDefault("Referer"),
                _currentHeaders.GetValueOrDefault("Origin"),
                new Dictionary<string, string>(_currentHeaders, StringComparer.OrdinalIgnoreCase),
                cookies,
                DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Auth cookies for some sites live on sibling hosts (e.g. Google account cookies for YouTube).
    /// </summary>
    private static IEnumerable<string> RelatedCookieUrls(Uri pageUrl)
    {
        var host = pageUrl.Host;
        if (host.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("google", StringComparison.OrdinalIgnoreCase))
        {
            yield return "https://www.youtube.com/";
            yield return "https://youtube.com/";
            yield return "https://www.google.com/";
            yield return "https://accounts.google.com/";
        }

        if (host.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("amemv", StringComparison.OrdinalIgnoreCase))
        {
            yield return "https://www.douyin.com/";
            yield return "https://www.iesdouyin.com/";
        }

        if (host.Contains("bilibili", StringComparison.OrdinalIgnoreCase))
        {
            yield return "https://www.bilibili.com/";
            yield return "https://bilibili.com/";
        }

        if (host.Contains("tiktok", StringComparison.OrdinalIgnoreCase))
        {
            yield return "https://www.tiktok.com/";
            yield return "https://tiktok.com/";
            yield return "https://www.tiktokv.com/";
            yield return "https://api16-normal-c-useast1a.tiktokv.com/";
        }
    }

    /// <summary>
    /// WebView2 session cookies often use DateTime.MinValue/MaxValue for Expires.
    /// Implicit conversion to DateTimeOffset throws ArgumentOutOfRangeException near those bounds.
    /// </summary>
    private static DateTimeOffset? ToCookieExpiry(DateTime expires)
    {
        if (expires.Year is < 1970 or > 9000)
            return null;

        try
        {
            var utc = expires.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(expires, DateTimeKind.Utc)
                : expires.ToUniversalTime();
            if (utc < DateTimeOffset.MinValue.UtcDateTime || utc > DateTimeOffset.MaxValue.UtcDateTime)
                return null;
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private async Task RefreshCurrentHeadersAsync()
    {
        if (_core is null)
            return;

        _currentHeaders["User-Agent"] = _core.Settings.UserAgent;
        if (_core.Source is not null)
            _currentHeaders["Referer"] = _core.Source;

        await Task.CompletedTask;
    }


    private async Task EnableCdpNetworkAsync(CancellationToken ct)
    {
        if (_core is null || _cdpEnabled)
            return;

        _cdpAttachedReceiver = _core.GetDevToolsProtocolEventReceiver("Target.attachedToTarget");
        _cdpAttachedReceiver.DevToolsProtocolEventReceived += OnCdpAttachedToTarget;

        // Flatten OOPIF / player iframe targets so cross-origin HLS (MacCMS etc.) is visible.
        try
        {
            await _core.CallDevToolsProtocolMethodAsync(
                "Target.setAutoAttach",
                """{"autoAttach":true,"waitForDebuggerOnStart":false,"flatten":true}""");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Target.setAutoAttach unavailable; continuing with top-level Network only");
        }

        await _core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        _cdpRequestReceiver = _core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        _cdpResponseReceiver = _core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        _cdpRequestReceiver.DevToolsProtocolEventReceived += OnCdpRequestWillBeSent;
        _cdpResponseReceiver.DevToolsProtocolEventReceived += OnCdpResponseReceived;
        _cdpFinishedReceiver = _core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        _cdpFailedReceiver = _core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        _cdpFinishedReceiver.DevToolsProtocolEventReceived += OnCdpLoadingFinished;
        _cdpFailedReceiver.DevToolsProtocolEventReceived += OnCdpLoadingFailed;
        _cdpEnabled = true;
    }

    private async void OnCdpAttachedToTarget(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var session = doc.RootElement.GetProperty("sessionId").GetString()!;
            if (_core is null) return;
            await _core.CallDevToolsProtocolMethodForSessionAsync(session, "Network.enable", "{}");
            await _core.CallDevToolsProtocolMethodForSessionAsync(session, "Target.setAutoAttach",
                """{"autoAttach":true,"waitForDebuggerOnStart":false,"flatten":true}""");
            _logger.LogInformation("Child target network enabled type={Type}",
                doc.RootElement.GetProperty("targetInfo").GetProperty("type").GetString());
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Child target network setup failed"); }
    }

    private static string CdpRequestKey(string? session, string id) =>
        string.IsNullOrEmpty(session) ? id : session + ":" + id;

    private async Task NotifyCurrentDocumentIdentityAsync()
    {
        try
        {
            var scriptJson = await ExecuteProbeScriptAsync(CancellationToken.None);
            var pageUrl = ExtractPageUrl(scriptJson) ?? CurrentPageUrl;
            if (pageUrl is null)
                return;

            _lastDocumentUrl = pageUrl;
            NotifyPageIdentityChanged(pageUrl, ExtractPageTitle(scriptJson) ?? _core?.DocumentTitle);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Current document identity refresh failed.");
        }
    }

    private void NotifyPageIdentityChanged(Uri pageUrl, string? pageTitle)
    {
        var key = $"{pageUrl.AbsoluteUri}\n{pageTitle}";
        if (string.Equals(_lastPageNotificationKey, key, StringComparison.Ordinal))
            return;

        _lastPageNotificationKey = key;
        PageIdentityChanged?.Invoke(this, new PageIdentityChangedEventArgs(pageUrl, pageTitle));
    }

    private static Uri? ExtractPageUrl(string? scriptJson)
    {
        using var doc = SiteJson(scriptJson);
        if (doc?.RootElement.TryGetProperty("href", out var href) == true &&
            Uri.TryCreate(href.GetString(), UriKind.Absolute, out var pageUrl))
        {
            return pageUrl;
        }

        return null;
    }

    private static string? ExtractPageTitle(string? scriptJson)
    {
        using var doc = SiteJson(scriptJson);
        if (doc is null)
            return null;

        foreach (var name in new[] { "caption", "ogTitle", "title" })
        {
            if (doc.RootElement.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        }

        return null;
    }

    private static JsonDocument? SiteJson(string? scriptJson)
    {
        if (string.IsNullOrWhiteSpace(scriptJson) || scriptJson == "null")
            return null;

        try
        {
            return JsonDocument.Parse(scriptJson);
        }
        catch
        {
            return null;
        }
    }

    private static string? DecodeScriptResult(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "null")
            return null;

        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? doc.RootElement.GetString()
                : value;
        }
        catch
        {
            return value;
        }
    }

    private void OnCdpRequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        => ProcessCdpRequest(e.ParameterObjectAsJson, e.SessionId);

    internal void ProcessCdpRequest(string json, string? cdpSession = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var requestId = CdpRequestKey(cdpSession, root.GetProperty("requestId").GetString() ?? Guid.NewGuid().ToString());
            var request = root.GetProperty("request");
            var url = request.GetProperty("url").GetString();
            if (string.IsNullOrEmpty(url) || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                return;

            var method = request.GetProperty("method").GetString() ?? "GET";
            var headers = ParseHeaders(request);
            var resourceType = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            var frameId = root.TryGetProperty("frameId", out var frameEl) ? frameEl.GetString() : null;

            _pendingRequests.TryRemove(requestId, out var redirected);
            var scope = redirected?.Scope;
            if (redirected is not null && redirected.SessionId != _pipeline.SessionId)
            { scope?.Dispose(); scope = null; }
            if (_captureEnabled && (resourceType is "Media" or "XHR" or "Fetch" ||
                ((resourceType is "Script" or "Document") && CurrentPageUrl is { } documentPage &&
                 VideoDownloader.Infrastructure.Detection.MediaOwnership.ForPage(documentPage, CurrentMediaSessionKey) is null) ||
                VideoDownloader.Infrastructure.Detection.UnifiedMediaPipeline.IsCandidate(new Uri(url), null)))
                scope ??= _pipeline.BeginDiscovery(_pipeline.SessionId);
            var pending = new PendingRequest(
                requestId,
                new Uri(url),
                method,
                headers,
                resourceType,
                frameId,
                CurrentPageUrl, _pipeline.SessionId) { Scope = scope };
            _pendingRequests[requestId] = pending;
            _ = ExpireRequestAsync(requestId, pending);

            foreach (var h in headers)
                _currentHeaders[h.Key] = h.Value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CDP requestWillBeSent parse failed");
        }
    }

    private void ClearPendingRequests()
    {
        foreach (var key in _pendingRequests.Keys)
            if (_pendingRequests.TryRemove(key, out var pending)) pending.Scope?.Dispose();
    }

    private async Task ExpireRequestAsync(string id, PendingRequest pending)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (((ICollection<KeyValuePair<string, PendingRequest>>)_pendingRequests).Remove(new(id, pending)))
            pending.Scope?.Dispose();
    }

    private void OnCdpResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        => ProcessCdpResponse(e.ParameterObjectAsJson, e.SessionId);

    internal void ProcessCdpResponse(string json, string? cdpSession = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var requestId = CdpRequestKey(cdpSession, root.GetProperty("requestId").GetString() ?? string.Empty);
            if (!_pendingRequests.TryRemove(requestId, out var pending))
                return;
            using var requestScope = pending.Scope;

            var response = root.GetProperty("response");
            var status = response.GetProperty("status").GetInt32();
            var mime = response.TryGetProperty("mimeType", out var mimeEl) ? mimeEl.GetString() : null;
            var headers = ParseHeaders(response);
            long? contentLength = null;
            if (headers.TryGetValue("content-length", out var cl) && long.TryParse(cl, out var len))
                contentLength = len;

            var raw = RawNetworkEvent.FromCdp(
                pending.Url,
                pending.Method,
                status,
                mime,
                contentLength,
                pending.ResourceType,
                null,
                pending.PageUrl,
                pending.FrameId,
                requestId,
                pending.RequestHeaders,
                headers);

            EnqueueRawEvent(raw with { SessionId = pending.SessionId }, requestScope?.Fork());
            // Exclusive hosts skip generic body scanning — except Douyin aweme/detail JSON,
            // which is the primary address-discovery method for that detector.
            var scanPage = pending.PageUrl ?? CurrentPageUrl;
            var douyinAwemeDetail = scanPage is not null &&
                                    IsDouyinHost(scanPage) &&
                                    IsAwemeDetailApi(raw.Url);
            if (_captureEnabled &&
                (scanPage is null || !IsExclusiveMediaHost(scanPage) || douyinAwemeDetail) &&
                ShouldScanResponseBody(raw) && raw.ContentLength is null or < 2097152 &&
                (requestScope?.Fork() ?? _pipeline.BeginDiscovery(pending.SessionId)) is { } scope)
            {
                _responseBodies[requestId] = (raw with { SessionId = pending.SessionId }, scope);
                _ = ExpireResponseBodyAsync(requestId, scope);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CDP responseReceived parse failed");
        }
    }


    private async Task ExpireResponseBodyAsync(string requestId, IDiscoveryScope scope)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (_responseBodies.TryRemove(requestId, out var pending)) pending.Scope.Dispose();
    }

    private void OnCdpLoadingFailed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
        var nativeId = doc.RootElement.GetProperty("requestId").GetString()!;
        var id = CdpRequestKey(e.SessionId, nativeId);
        if (_responseBodies.TryRemove(id, out var pending)) pending.Scope.Dispose();
        if (_pendingRequests.TryRemove(id, out var request)) request.Scope?.Dispose();
    }

    private async void OnCdpLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var nativeId = doc.RootElement.GetProperty("requestId").GetString()!;
            var id = CdpRequestKey(e.SessionId, nativeId);
            if (!_responseBodies.TryRemove(id, out var pending)) return;
            using var scope = pending.Scope;
            if (_core is null || !_captureEnabled || pending.Event.SessionId != _pipeline.SessionId ||
                doc.RootElement.GetProperty("encodedDataLength").GetDouble() > 2097152) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _core.CallDevToolsProtocolMethodForSessionAsync(e.SessionId, "Network.getResponseBody",
                JsonSerializer.Serialize(new { requestId = nativeId })).WaitAsync(timeout.Token);
            using var payload = JsonDocument.Parse(response);
            var body = payload.RootElement.GetProperty("body").GetString() ?? "";
            if (body.Length > 2796204) return;
            if (payload.RootElement.GetProperty("base64Encoded").GetBoolean())
                body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body));
            if (body.Length > 2097152 || pending.Event.PageUrl is not { } page) return;
            var currentOwner = VideoDownloader.Infrastructure.Detection.MediaOwnership.ForPage(page, CurrentMediaSessionKey);
            var requestOwner = VideoDownloader.Infrastructure.Detection.MediaOwnership.ForPage(pending.Event.Url, null);
            var addresses = VideoDownloader.Infrastructure.Detection.MediaAddressScanner.ScanResponse(body, currentOwner,
                requestOwner == currentOwner ? requestOwner : null);
            _logger.LogInformation("Response discovery session={Session} host={Host} candidates={Count}", pending.Event.SessionId, pending.Event.Url.Host, addresses.Count);
            if (addresses.Count > 0 && _captureEnabled)
            {
                var probeMethod = IsAwemeDetailApi(pending.Event.Url)
                    ? VideoDownloader.Core.Contracts.ProbeMethods.AwemeDetail
                    : null;
                await scope.SubmitAsync(page, JsonSerializer.Serialize(new
                {
                    probeMethod,
                    candidates = addresses.Select(a => new { url = a.Url, contentIdentity = a.ContentIdentity })
                }),
                    CaptureCurrentContext(page,page), timeout.Token);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Bounded response discovery failed"); }
    }

    private static bool ShouldScanResponseBody(RawNetworkEvent raw)
    {
        if (raw.MimeType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (raw.MimeType?.Contains("javascript", StringComparison.OrdinalIgnoreCase) == true ||
            raw.MimeType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true) return true;

        var url = raw.Url.AbsoluteUri;
        return url.Contains("/aweme/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("iteminfo", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/detail", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/feed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExclusiveMediaHost(Uri pageUrl)
    {
        var host = pageUrl.Host;
        return host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDouyinHost(Uri pageUrl) =>
        pageUrl.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
        pageUrl.Host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsAwemeDetailApi(Uri url)
    {
        var path = url.AbsolutePath;
        return path.Contains("/aweme/v1/web/aweme/detail", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/aweme/detail", StringComparison.OrdinalIgnoreCase) ||
               (path.Contains("/aweme/", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("detail", StringComparison.OrdinalIgnoreCase));
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(ct))
        {
            var raw = item.Event;
            using var scope = item.Scope;
            try
            {
                if (raw.SessionId != _pipeline.SessionId || !_captureEnabled) continue;
                var normalized = await _normalizer.NormalizeAsync(raw, ct);
                if (normalized is null || raw.SessionId != _pipeline.SessionId || !_captureEnabled)
                    continue;

                var enriched = normalized with
                {
                    RequestContext = normalized.RequestContext with { Cookies = CaptureCurrentContext(normalized.PageUrl, normalized.Url).Cookies }
                };

                await scope.ProcessAsync(enriched, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Network event pipeline failed for {Url}",
                    SanitizedLogger.SanitizeUrl(raw.Url.ToString()));
            }
        }
    }

    private void EnqueueRawEvent(RawNetworkEvent raw, IDiscoveryScope? reserved = null)
    {
        if (!_captureEnabled || raw.SessionId != _pipeline.SessionId)
        {
            reserved?.Dispose();
            return;
        }

        // Only primary (non-segment) media can switch sessions.
        if (IsPrimarySessionMedia(raw))
            ObserveMediaCandidate(raw.Url, "network", raw.MimeType, raw.ContentLength);

        var scope = reserved ?? _pipeline.BeginDiscovery(raw.SessionId);
        if (scope is null) return;
        if (_events.Writer.TryWrite((raw, scope)))
            return;

        if (IsHighValueEvent(raw))
        {
            _ = PreserveHighValueEventAsync(raw, scope);
            return;
        }

        scope.Dispose();
        _logger.LogDebug(
            "Dropped low-value network event under backpressure: {Url}",
            SanitizedLogger.SanitizeUrl(raw.Url.ToString()));
    }

    private async Task PreserveHighValueEventAsync(RawNetworkEvent raw, IDiscoveryScope scope)
    {
        var written = false;
        try
        {
            await _events.Writer.WriteAsync((raw, scope), _consumerCts?.Token ?? CancellationToken.None);
            written = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to preserve high-value network event: {Url}",
                SanitizedLogger.SanitizeUrl(raw.Url.ToString()));
        }
        finally { if (!written) scope.Dispose(); }
    }

    private static bool IsPrimarySessionMedia(RawNetworkEvent raw)
    {
        if (VideoDownloader.Infrastructure.Detection.MediaUrlNormalizer.IsLikelySegment(raw.Url))
            return false;

        if (IsHighValueEvent(raw))
            return true;

        return VideoDownloader.Infrastructure.Detection.UnifiedMediaPipeline.IsCandidate(
            raw.Url, raw.MimeType, raw.ResourceType, raw.ContentLength);
    }

    private static bool IsHighValueEvent(RawNetworkEvent raw)
    {
        if (raw.MimeType is not null &&
            (raw.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
             raw.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
             raw.MimeType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
             raw.MimeType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)))
            return true;

        var path = raw.Url.AbsolutePath;
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
               raw.Url.AbsoluteUri.Contains("videoplayback", StringComparison.OrdinalIgnoreCase) ||
               raw.Url.AbsoluteUri.Contains("mime=video", StringComparison.OrdinalIgnoreCase) ||
               raw.Url.AbsoluteUri.Contains("mime=audio", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseHeaders(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!element.TryGetProperty("headers", out var headers))
            return result;

        foreach (var prop in headers.EnumerateObject())
            result[prop.Name] = prop.Value.GetString() ?? string.Empty;

        return result;
    }

    private static long? TryParseContentLength(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("Content-Length", out var value) && long.TryParse(value, out var len))
            return len;
        return null;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _consumerCts?.Cancel();
        _events.Writer.TryComplete();

        if (_consumerTask is not null)
        {
            try
            {
                await Task.WhenAny(_consumerTask, Task.Delay(1500));
            }
            catch
            {
                // ignore shutdown race
            }
        }

        if (_core is not null)
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
        }

        if (_cdpAttachedReceiver is not null)
            _cdpAttachedReceiver.DevToolsProtocolEventReceived -= OnCdpAttachedToTarget;
        if (_cdpRequestReceiver is not null)
            _cdpRequestReceiver.DevToolsProtocolEventReceived -= OnCdpRequestWillBeSent;

        if (_cdpResponseReceiver is not null)
            _cdpResponseReceiver.DevToolsProtocolEventReceived -= OnCdpResponseReceived;
        if (_cdpFinishedReceiver is not null)
            _cdpFinishedReceiver.DevToolsProtocolEventReceived -= OnCdpLoadingFinished;
        if (_cdpFailedReceiver is not null)
            _cdpFailedReceiver.DevToolsProtocolEventReceived -= OnCdpLoadingFailed;
        ResetDetectionSession();
        while (_events.Reader.TryRead(out var queued)) queued.Scope.Dispose();

        ResetMediaSessionAnchor();
        _consumerCts?.Dispose();
    }

    private sealed record ContextSnapshot(
        string? UserAgent,
        string? Referer,
        string? Origin,
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyList<BrowserCookie> Cookies,
        DateTimeOffset CapturedAt)
    {
        public static ContextSnapshot Empty { get; } = new(
            null,
            null,
            null,
            new Dictionary<string, string>(),
            Array.Empty<BrowserCookie>(),
            DateTimeOffset.MinValue);
    }

    private sealed record PendingRequest(
        string RequestId,
        Uri Url,
        string Method,
        Dictionary<string, string> RequestHeaders,
        string? ResourceType,
        string? FrameId,
        Uri? PageUrl,
        Guid SessionId)
    {
        public IDiscoveryScope? Scope { get; init; }
    }
}

public sealed record PageIdentityChangedEventArgs(Uri PageUrl, string? PageTitle);

public sealed record MediaSessionChangedEventArgs(
    Uri PageUrl,
    string? PageTitle,
    string MediaSessionKey,
    string PreviousMediaSessionKey,
    string Source,
    bool ForceRestart = false);
