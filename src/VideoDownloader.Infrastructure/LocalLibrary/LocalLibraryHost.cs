using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;

namespace VideoDownloader.Infrastructure.LocalLibrary;

/// <summary>
/// Gallery for completed downloads. Serves HTML cards, Range streams, and thumbs on loopback and LAN.
/// </summary>
public sealed class LocalLibraryHost : IAsyncDisposable
{
    public const int PreferredPort = 17890;
    private readonly IDownloadRepository _repository;
    private readonly IDownloadEngine _engine;
    private readonly ILibraryKindCatalog _kinds;
    private readonly LocalVideoThumbnailStore _thumbs;
    private readonly ILogger<LocalLibraryHost> _logger;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private List<LibraryItem>? _completedCache;
    private DateTimeOffset _completedCacheExpiry = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private sealed record PrivateSession(string Kind, DateTimeOffset ExpiresAt, byte[] LegacyKeyMaterial);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PrivateSession> _privateSessions = new();

    private PrivateSession? GetPrivateSession(HttpListenerRequest request, string kind)
    {
        var token = request.Cookies[$"vd_unlocked_{kind}"]?.Value;
        return token is not null && _privateSessions.TryGetValue(token, out var session) &&
            session.Kind == kind && session.ExpiresAt > DateTimeOffset.UtcNow ? session : null;
    }

    private void RevokePrivateSession(HttpListenerRequest request, string kind)
    {
        var token = request.Cookies[$"vd_unlocked_{kind}"]?.Value;
        if (token is not null) _privateSessions.TryRemove(token, out _);
    }

    public LocalLibraryHost(
        IDownloadRepository repository,
        IDownloadEngine engine,
        ILibraryKindCatalog kinds,
        LocalVideoThumbnailStore thumbs,
        ILogger<LocalLibraryHost> logger)
    {
        _repository = repository;
        _engine = engine;
        _kinds = kinds;
        _thumbs = thumbs;
        _logger = logger;
    }

    public Uri? BaseUri { get; private set; }
    public bool IsRunning => _listener?.IsListening == true;

    public static bool IsLocalLibraryHost(Uri? uri)
    {
        if (uri is null || uri.Scheme is not ("http" or "https"))
            return false;
        return uri.Host is "127.0.0.1" or "localhost" or "::1";
    }

    public static bool IsLocalPlayerUrl(Uri? uri) =>
        IsLocalLibraryHost(uri) &&
        uri!.AbsolutePath.StartsWith("/play/", StringComparison.OrdinalIgnoreCase);

    public string GalleryUrl
    {
        get
        {
            var root = (BaseUri ?? new Uri($"http://127.0.0.1:{PreferredPort}/")).AbsoluteUri;
            // Cold start / “本地视频” always open time-ordered gallery (not last site/kind mode).
            return root.Contains('?', StringComparison.Ordinal) ? root : root + "?group=time";
        }
    }

    public string PlayUrl(Guid jobId) =>
        new Uri(BaseUri ?? new Uri($"http://127.0.0.1:{PreferredPort}/"), $"play/{jobId:N}").AbsoluteUri;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
            return;



        var networkReady = await LocalLibraryNetworkAccess.EnsureConfiguredAsync(PreferredPort, _logger, ct);
        Exception? last = null;
        // Keep the advertised port stable. A different port would invalidate the firewall rule and shared URLs.
        var port = PreferredPort;
        {
            if (networkReady)
            {
                var dualListener = new HttpListener();
                // A strong wildcard also handles 10/8, 172.16/12, VPN adapters and later IP changes.
                dualListener.Prefixes.Add(LocalLibraryNetworkAccess.GetPrefix(port));
                try
                {
                    dualListener.Start();
                    _listener = dualListener;
                    BaseUri = new Uri($"http://127.0.0.1:{port}/");
                    _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
                    _logger.LogInformation("Local library listening on all interfaces at port {Port}", port);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.LogWarning(ex, "Local library LAN listener failed on port {Port}; trying loopback", port);
                    try { dualListener.Close(); } catch { /* ignore */ }
                }
            }

            var loopbackListener = new HttpListener();
            loopbackListener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                loopbackListener.Start();
                _listener = loopbackListener;
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
                _logger.LogInformation("Local library listening at 127.0.0.1:{Port} (loopback only)", port);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { loopbackListener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException(
            $"Unable to bind local library on port {PreferredPort}.", last);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* ignore */ }
            }

            if (_listener is not null)
            {
                try { _listener.Stop(); } catch { /* ignore */ }
                try { _listener.Close(); } catch { /* ignore */ }
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _listener = null;
            _loop = null;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null)
            return;

        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Local library accept failed");
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/", StringComparison.Ordinal) ||
                path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                // A new gallery visit must not reuse an earlier page's private-category unlock.
                foreach (var kind in _kinds.GetAll().Where(k => k.IsPrivate))
                {
                    RevokePrivateSession(ctx.Request, kind.Value);
                    ctx.Response.SetCookie(new Cookie($"vd_unlocked_{kind.Value}", string.Empty, "/")
                    {
                        HttpOnly = true,
                        Expires = DateTime.UtcNow.AddDays(-1)
                    });
                }
                ctx.Response.Headers["Cache-Control"] = "no-store";
                await WriteHtmlAsync(ctx, LocalLibraryPages.GalleryHtml);
                return;
            }

            if (path.StartsWith("/play/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Local library play page requested path={Path}", path);
                await WriteHtmlAsync(ctx, LocalLibraryPages.PlayerHtml);
                return;
            }

            if (path.Equals("/api/videos", StringComparison.OrdinalIgnoreCase))
            {
                await WriteVideosJsonAsync(ctx);
                return;
            }

            if (path.Equals("/api/kinds", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleAddKindAsync(ctx);
                    return;
                }

                await WriteJsonAsync(ctx, _kinds.GetAll().Select(x => new { value = x.Value, label = x.Label, isPrivate = x.IsPrivate }));
                return;
            }

            if (path.Equals("/api/privacy/verify", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleVerifyPrivacyPasswordAsync(ctx);
                return;
            }

            if (path.Equals("/api/privacy/unlock", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUnlockPrivateKindAsync(ctx);
                return;
            }

            if (path.Equals("/api/privacy/lock", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleLockPrivateKindAsync(ctx);
                return;
            }

            if (path.Equals("/api/privacy/unprotect", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUnprotectAsync(ctx);
                return;
            }

            if (path.Equals("/api/edit", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEditAsync(ctx);
                return;
            }

            if (path.StartsWith("/api/thumb/", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(path["/api/thumb/".Length..].TrimEnd('/'), "N", out var thumbId))
            {
                await WriteThumbAsync(ctx, thumbId);
                return;
            }

            if (path.StartsWith("/api/stream/", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(path["/api/stream/".Length..].TrimEnd('/'), "N", out var streamId))
            {
                await WriteStreamAsync(ctx, streamId);
                return;
            }

            if (path.Equals("/api/item", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(ctx.Request.QueryString["id"], out var itemId))
            {
                await WriteItemJsonAsync(ctx, itemId);
                return;
            }

            ctx.Response.StatusCode = 404;
            await WriteTextAsync(ctx, "not found");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local library request failed");
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteTextAsync(ctx, "error");
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { /* ignore */ }
        }
    }

    private async Task HandleAddKindAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var raw = await reader.ReadToEndAsync();
        AddKindRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<AddKindRequest>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "bad json");
            return;
        }

        try
        {
            var entry = await _kinds.AddAsync(req?.Label ?? string.Empty, req?.IsPrivate == true, req?.Password, CancellationToken.None);
            await WriteJsonAsync(ctx, new { value = entry.Value, label = entry.Label, isPrivate = entry.IsPrivate });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, ex.Message);
        }
    }

    private async Task HandleEditAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var raw = await reader.ReadToEndAsync();
        EditRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<EditRequest>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "bad json");
            return;
        }

        if (req is null || !Guid.TryParse(req.Id, out var id))
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "missing id");
            return;
        }

        try
        {
            var job = await _repository.GetByIdAsync(id, CancellationToken.None);
            if (job is null)
            {
                ctx.Response.StatusCode = 404;
                await WriteTextAsync(ctx, "missing");
                return;
            }

            await _engine.UpdateLibraryItemAsync(
                id, req.TitleHead, req.Caption, req.VideoKind, CancellationToken.None);
            _completedCacheExpiry = DateTimeOffset.MinValue;
            var items = await LoadCompletedAsync();
            var item = items.FirstOrDefault(i => i.Id == id);
            if (item is null)
            {
                ctx.Response.StatusCode = 404;
                await WriteTextAsync(ctx, "missing");
                return;
            }

            await WriteJsonAsync(ctx, item);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, ex.Message);
        }
    }

    private async Task HandleVerifyPrivacyPasswordAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var raw = await reader.ReadToEndAsync();
        PrivacyVerifyRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<PrivacyVerifyRequest>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "bad json");
            return;
        }

        if (string.IsNullOrWhiteSpace(req?.KindValue))
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "missing kindValue");
            return;
        }

        var ok = await _kinds.VerifyPasswordAsync(req.KindValue, req?.Password ?? string.Empty, CancellationToken.None);
        if (!ok)
        {
            ctx.Response.StatusCode = 403;
            await WriteTextAsync(ctx, "密码错误");
            return;
        }

        await WriteJsonAsync(ctx, new { ok = true });
    }

    private async Task HandleUnlockPrivateKindAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var raw = await reader.ReadToEndAsync();
        PrivacyVerifyRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<PrivacyVerifyRequest>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "bad json");
            return;
        }

        if (string.IsNullOrWhiteSpace(req?.KindValue))
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "missing kindValue");
            return;
        }

        var ok = await _kinds.VerifyPasswordAsync(req.KindValue, req?.Password ?? string.Empty, CancellationToken.None);
        if (!ok)
        {
            ctx.Response.StatusCode = 403;
            await WriteTextAsync(ctx, "密码错误");
            return;
        }

        // Older encryption renamed files without persisting their replacement paths.
        // Repair through the engine so its tracked jobs and the database stay in sync.
        try
        {
            await _engine.RepairPrivateLibraryAsync(req.KindValue);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Private library record recovery failed");
            ctx.Response.StatusCode = 500;
            await WriteTextAsync(ctx, "恢复隐私视频记录失败，请重试");
            return;
        }

        // Set cookie for this client only
        var cookieName = $"vd_unlocked_{req.KindValue}";
        var cookieValue = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        RevokePrivateSession(ctx.Request, req.KindValue);
        foreach (var expired in _privateSessions.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow))
            _privateSessions.TryRemove(expired.Key, out _);
        _privateSessions[cookieValue] = new PrivateSession(req.KindValue, DateTimeOffset.UtcNow.AddDays(1),
            VideoEncryptionService.DeriveLegacyKeyMaterial(req.Password ?? string.Empty));
        ctx.Response.SetCookie(new Cookie(cookieName, cookieValue, "/")
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(1)
        });

        _completedCacheExpiry = DateTimeOffset.MinValue;
        await WriteJsonAsync(ctx, new { ok = true });
    }

    private async Task HandleLockPrivateKindAsync(HttpListenerContext ctx)
    {
        PrivacyVerifyRequest? req;
        try
        {
            req = await JsonSerializer.DeserializeAsync<PrivacyVerifyRequest>(ctx.Request.InputStream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "bad json");
            return;
        }
        if (string.IsNullOrWhiteSpace(req?.KindValue) || !_kinds.GetAll().Any(k => k.Value == req.KindValue && k.IsPrivate))
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "invalid kindValue");
            return;
        }
        RevokePrivateSession(ctx.Request, req.KindValue);
        ctx.Response.SetCookie(new Cookie($"vd_unlocked_{req.KindValue}", string.Empty, "/")
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(-1)
        });
        _completedCacheExpiry = DateTimeOffset.MinValue;
        await WriteJsonAsync(ctx, new { ok = true });
    }

    private async Task HandleUnprotectAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var raw = await reader.ReadToEndAsync();
        UnprotectRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<UnprotectRequest>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "bad json");
            return;
        }

        if (req is null || !Guid.TryParse(req.Id, out var id))
        {
            ctx.Response.StatusCode = 400;
            await WriteTextAsync(ctx, "missing id");
            return;
        }

        var job = await _repository.GetByIdAsync(id, CancellationToken.None);
        if (job is null || !job.IsEncrypted)
        {
            ctx.Response.StatusCode = 404;
            await WriteTextAsync(ctx, "未找到加密文件");
            return;
        }

        var kind = _kinds.Normalize(job.VideoKind);
        var ok = await _kinds.VerifyPasswordAsync(kind, req.Password ?? string.Empty, CancellationToken.None);
        if (!ok)
        {
            ctx.Response.StatusCode = 403;
            await WriteTextAsync(ctx, "密码错误");
            return;
        }

        try
        {
            await _engine.UpdateLibraryItemAsync(id, videoKind: LibraryVideoKinds.Unspecified, ct: CancellationToken.None);

            _completedCacheExpiry = DateTimeOffset.MinValue;
            await WriteJsonAsync(ctx, new { ok = true });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await WriteTextAsync(ctx, ex.Message);
        }
    }

    private async Task WriteVideosJsonAsync(HttpListenerContext ctx)
    {
        var groupMode = (ctx.Request.QueryString["group"] ?? "").Trim().ToLowerInvariant();
        var items = (await LoadCompletedAsync()).ToList();

        // Get unlocked kinds from cookies (client-specific)
        var unlockedKinds = GetUnlockedKindsFromCookies(ctx.Request);

        bool IsPrivateKind(string? kindValue)
        {
            if (string.IsNullOrEmpty(kindValue)) return false;
            return _kinds.GetAll().FirstOrDefault(k => k.Value == kindValue)?.IsPrivate == true;
        }

        bool IsUnlocked(string? kindValue)
        {
            if (string.IsNullOrEmpty(kindValue)) return false;
            return unlockedKinds.Contains(kindValue);
        }

        // For non-kind group modes, exclude private videos unless unlocked
        // For kind group mode, include all items (BuildKindGroups will handle filtering)
        var filteredItems = items.Where(i =>
        {
            if (!IsPrivateKind(i.VideoKind))
                return true;
            if (groupMode == "kind")
                return true; // BuildKindGroups will filter based on unlock status
            return IsUnlocked(i.VideoKind);
        }).ToList();

        var authorFilter = ctx.Request.QueryString["author"];
        if (authorFilter is not null)
        {
            // Apply privacy checks first, including when the URL also requests kind groups.
            var visible = filteredItems.Where(i => !IsPrivateKind(i.VideoKind) || IsUnlocked(i.VideoKind));
            await WriteJsonAsync(ctx, LocalLibraryOrdering.ForAuthor(visible, authorFilter,
                i => i.Author, i => i.TitleHead).ToArray());
            return;
        }

        var timeOrdered = filteredItems.OrderByDescending(i => i.DownloadedAt).ToList();

        object payload = groupMode switch
        {
            "site" => filteredItems.GroupBy(i => i.SiteGroup)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    group = g.Key,
                    items = LocalLibraryOrdering
                        .OrderByAuthorThenDownloadedAtDesc(g, i => i.Author, i => i.DownloadedAt)
                        .ToArray()
                })
                .ToArray(),
            "kind" => BuildKindGroups(items, unlockedKinds), // Pass all items, let BuildKindGroups filter
            _ => timeOrdered
        };

        await WriteJsonAsync(ctx, payload);
    }

    private HashSet<string> GetUnlockedKindsFromCookies(HttpListenerRequest request)
    {
        var unlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allKinds = _kinds.GetAll().Where(k => k.IsPrivate);

        foreach (var kind in allKinds)
        {
            if (GetPrivateSession(request, kind.Value) is not null)
            {
                unlocked.Add(kind.Value);
            }
        }

        return unlocked;
    }

    private object BuildKindGroups(List<LibraryItem> items, HashSet<string>? unlockedKinds)
    {
        var allKinds = _kinds.GetAll();
        var kindByLabel = allKinds.ToDictionary(k => k.Label, k => k, StringComparer.OrdinalIgnoreCase);

        var grouped = items.GroupBy(i => i.VideoKindLabel)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var allLabels = new HashSet<string>(grouped.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var kind in allKinds)
        {
            if (kind.IsPrivate)
                allLabels.Add(kind.Label);
        }

        var result = new List<object>();

        foreach (var label in allLabels)
        {
            kindByLabel.TryGetValue(label, out var kindEntry);
            var isPrivate = kindEntry?.IsPrivate == true;
            var isUnlocked = isPrivate && unlockedKinds != null && unlockedKinds.Contains(kindEntry!.Value);

            IEnumerable<LibraryItem> groupItems;
            if (grouped.TryGetValue(label, out var group))
            {
                groupItems = isPrivate && !isUnlocked
                    ? Array.Empty<LibraryItem>()
                    : LocalLibraryOrdering.OrderByAuthorThenDownloadedAtDesc(group, i => i.Author, i => i.DownloadedAt);
            }
            else
            {
                groupItems = Array.Empty<LibraryItem>();
            }

            result.Add(new
            {
                group = label,
                isPrivate,
                isUnlocked,
                items = groupItems.ToArray()
            });
        }

        return result
            .OrderBy(g =>
            {
                var name = (string)g.GetType().GetProperty("group")!.GetValue(g)!;
                return name == "未分类" ? 1 : 0;
            })
            .ThenBy(g => (string)g.GetType().GetProperty("group")!.GetValue(g)!, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task WriteItemJsonAsync(HttpListenerContext ctx, Guid id)
    {
        var items = await LoadCompletedAsync();
        var item = items.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            ctx.Response.StatusCode = 404;
            await WriteTextAsync(ctx, "missing");
            return;
        }

        ctx.Response.Headers["Cache-Control"] = "no-store";
        await WriteJsonAsync(ctx, item with { IsUnlocked = GetPrivateSession(ctx.Request, item.VideoKind) is not null });
    }

    private async Task WriteThumbAsync(HttpListenerContext ctx, Guid id)
    {
        var thumb = _thumbs.GetPath(id);
        if (_thumbs.Exists(id))
        {
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
            var bytes = await File.ReadAllBytesAsync(thumb);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            return;
        }

        var job = await _repository.GetByIdAsync(id);
        if (job is null || job.Status != DownloadStatus.Completed ||
            string.IsNullOrWhiteSpace(job.TargetPath) || !File.Exists(job.TargetPath))
        {
            ctx.Response.StatusCode = 404;
            await WriteTextAsync(ctx, "missing");
            return;
        }

        if (IsAudioOnlyJob(job))
        {
            await WriteAudioPlaceholderThumbAsync(ctx);
            return;
        }

        _thumbs.EnsureAsyncFireAndForget(id, job.TargetPath, async jobId =>
        {
            try
            {
                await _repository.UpdateThumbReadyAsync(jobId, true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update thumb_ready for {JobId}", jobId);
            }
        });
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentType = "image/svg+xml";
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='320' height='200'><rect fill='#1f2937' width='100%' height='100%'/><text x='50%' y='50%' fill='#9ca3af' font-size='14' text-anchor='middle' dy='.3em'>…</text></svg>");
        ctx.Response.ContentLength64 = svg.Length;
        await ctx.Response.OutputStream.WriteAsync(svg);
    }

    private static async Task WriteAudioPlaceholderThumbAsync(HttpListenerContext ctx)
    {
        ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
        ctx.Response.ContentType = "image/svg+xml; charset=utf-8";
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='640' height='400' viewBox='0 0 640 400'>" +
            "<defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'>" +
            "<stop offset='0%' stop-color='#1e293b'/><stop offset='100%' stop-color='#0f172a'/>" +
            "</linearGradient></defs>" +
            "<rect fill='url(#g)' width='100%' height='100%'/>" +
            "<text x='50%' y='50%' fill='#e2e8f0' font-size='56' font-weight='700' " +
            "font-family='Microsoft YaHei,Segoe UI,sans-serif' text-anchor='middle' dy='.35em' " +
            "letter-spacing='0.18em'>音频</text></svg>");
        ctx.Response.ContentLength64 = svg.Length;
        await ctx.Response.OutputStream.WriteAsync(svg);
    }

    private async Task WriteStreamAsync(HttpListenerContext ctx, Guid id)
    {
        var job = await _repository.GetByIdAsync(id);
        if (job is null || job.Status != DownloadStatus.Completed ||
            string.IsNullOrWhiteSpace(job.TargetPath) || !File.Exists(job.TargetPath))
        {
            ctx.Response.StatusCode = 404;
            await WriteTextAsync(ctx, "missing");
            return;
        }

        var path = job.TargetPath;
        var isEncrypted = job.IsEncrypted;

        var privateKind = _kinds.GetAll().Any(k => k.Value == job.VideoKind && k.IsPrivate);
        var session = GetPrivateSession(ctx.Request, job.VideoKind ?? string.Empty);
        if ((isEncrypted || privateKind) && session is null)
        {
            ctx.Response.StatusCode = 403;
            await WriteTextAsync(ctx, "需要隐私密码");
            return;
        }
        if (isEncrypted)
        {
            DecryptedVideoStream? decStream = null;
            try
            {
                decStream = VideoEncryptionService.OpenDecryptedRead(path, session!.LegacyKeyMaterial);
            }
            catch
            {
                ctx.Response.StatusCode = 500;
                await WriteTextAsync(ctx, "解密失败");
                return;
            }

            var virtualLength = decStream.Length;
            var contentType = GuessContentType(job.OriginalFilename ?? path);
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentType = contentType;

            long start = 0;
            long end = virtualLength - 1;
            var range = ctx.Request.Headers["Range"];
            if (!string.IsNullOrWhiteSpace(range))
            {
                if (!System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed) ||
                    !parsed.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || parsed.Ranges.Count != 1)
                {
                    decStream.Dispose();
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers["Content-Range"] = $"bytes */{virtualLength}";
                    return;
                }
                var requested = parsed.Ranges.Single();
                start = requested.From ?? Math.Max(0, virtualLength - requested.To!.Value);
                end = requested.From is null ? virtualLength - 1 : Math.Min(requested.To ?? end, end);
                if (start > end || start >= virtualLength)
                {
                    decStream.Dispose();
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers["Content-Range"] = $"bytes */{virtualLength}";
                    return;
                }
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{virtualLength}";
            }

            var count = end - start + 1;
            ctx.Response.ContentLength64 = count;
            decStream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[256 * 1024];
            var remaining = count;
            try
            {
                while (remaining > 0)
                {
                    var read = await decStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                    if (read <= 0)
                        break;
                    await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                    remaining -= read;
                }
            }
            finally
            {
                decStream.Dispose();
            }
            return;
        }

        var length = new FileInfo(path).Length;
        var ct = GuessContentType(path);
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.ContentType = ct;

        long s2 = 0;
        long e2 = length - 1;
        var range2 = ctx.Request.Headers["Range"];
        if (!string.IsNullOrWhiteSpace(range2) &&
            range2.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range2[6..];
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var left = spec[..dash];
                var right = spec[(dash + 1)..];
                if (long.TryParse(left, out var s))
                    s2 = s;
                if (long.TryParse(right, out var e) && e >= s2)
                    e2 = e;
                e2 = Math.Min(e2, length - 1);
                if (s2 > e2 || s2 >= length)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers["Content-Range"] = $"bytes */{length}";
                    return;
                }

                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {s2}-{e2}/{length}";
            }
        }

        var cnt = e2 - s2 + 1;
        ctx.Response.ContentLength64 = cnt;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(s2, SeekOrigin.Begin);
        var buf = new byte[256 * 1024];
        var rem = cnt;
        while (rem > 0)
        {
            var read = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, rem)));
            if (read <= 0)
                break;
            await ctx.Response.OutputStream.WriteAsync(buf.AsMemory(0, read));
            rem -= read;
        }
    }

    private async Task<List<LibraryItem>> LoadCompletedAsync()
    {
        if (_completedCache is not null && DateTimeOffset.UtcNow < _completedCacheExpiry)
            return _completedCache;

        var all = await _repository.GetAllAsync();
        var list = new List<LibraryItem>();
        foreach (var job in all)
        {
            if (job.Status != DownloadStatus.Completed)
                continue;
            if (string.IsNullOrWhiteSpace(job.TargetPath) || !File.Exists(job.TargetPath))
                continue;

            var audioOnly = IsAudioOnlyJob(job);
            string thumbStatus;
            if (audioOnly)
            {
                thumbStatus = "audio";
            }
            else if (job.ThumbReady)
            {
                thumbStatus = "ready";
            }
            else
            {
                thumbStatus = "pending";
            }

            var fileName = Path.GetFileName(job.TargetPath);
            var displayName = job.IsEncrypted && !string.IsNullOrEmpty(job.OriginalFilename)
                ? job.OriginalFilename
                : fileName;
            var stem = Path.GetFileNameWithoutExtension(displayName);
            DownloadFileNameBuilder.TrySplitMetaSuffix(stem, out var titleHead, out var metaSuffix);
            if (string.IsNullOrWhiteSpace(titleHead))
                titleHead = stem;
            var caption = string.IsNullOrWhiteSpace(job.Caption) ? job.DisplayName : job.Caption!;
            var kind = _kinds.Normalize(job.VideoKind);
            var kindEntry = _kinds.GetAll().FirstOrDefault(k => k.Value == kind);
            var isPrivateKind = kindEntry?.IsPrivate == true;
            var downloadedAt = job.CompletedAt ?? job.UpdatedAt;
            var ext = Path.GetExtension(displayName);
            list.Add(new LibraryItem(
                job.Id,
                fileName,
                titleHead,
                metaSuffix,
                ext,
                caption,
                kind,
                _kinds.LabelOf(kind),
                downloadedAt,
                job.Author,
                DownloadSiteFolder.Resolve(job.PageUrl),
                $"/api/thumb/{job.Id:N}",
                $"/api/stream/{job.Id:N}",
                $"/play/{job.Id:N}",
                audioOnly,
                job.SubtitleRecognized,
                thumbStatus,
                job.IsEncrypted,
                isPrivateKind));
        }

        _completedCache = list;
        _completedCacheExpiry = DateTimeOffset.UtcNow.Add(CacheTtl);
        return list;
    }

    private static bool IsAudioOnlyJob(DownloadJob job)
    {
        var tracks = job.Variant?.Tracks;
        if (tracks is { Count: > 0 })
        {
            var hasVideo = tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined);
            if (hasVideo)
                return false;
            if (tracks.Any(t => t.Kind == MediaTrackKind.Audio))
                return true;
        }

        return IsAudioExtension(job.TargetPath);
    }

    private static bool IsAudioExtension(string? path)
    {
        var ext = Path.GetExtension(path ?? string.Empty);
        return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".opus", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".wma", StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".m4a" => "audio/mp4",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream"
        };

    private static async Task WriteHtmlAsync(HttpListenerContext ctx, string html)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, object payload)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteTextAsync(HttpListenerContext ctx, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private sealed class EditRequest
    {
        public string? Id { get; set; }
        public string? TitleHead { get; set; }
        public string? Caption { get; set; }
        public string? VideoKind { get; set; }
    }

    private sealed class AddKindRequest
    {
        public string? Label { get; set; }
        public bool? IsPrivate { get; set; }
        public string? Password { get; set; }
    }

    private sealed class PrivacyVerifyRequest
    {
        public string? KindValue { get; set; }
        public string? Password { get; set; }
    }

    private sealed class UnprotectRequest
    {
        public string? Id { get; set; }
        public string? Password { get; set; }
    }

    private sealed record LibraryItem(
        Guid Id,
        string FileName,
        string TitleHead,
        string MetaSuffix,
        string Extension,
        string Caption,
        string VideoKind,
        string VideoKindLabel,
        DateTimeOffset DownloadedAt,
        string? Author,
        string SiteGroup,
        string ThumbUrl,
        string StreamUrl,
        string PlayUrl,
        bool IsAudioOnly,
        bool SubtitleRecognized,
        string ThumbStatus,
        bool IsEncrypted,
        bool IsPrivateKind)
    {
        public bool IsUnlocked { get; init; }
        public string DownloadedAtText =>
            DownloadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        public string AuthorDisplay =>
            "作者：" + AuthorNameResolver.DisplayOrUnknown(Author);
    }
}
