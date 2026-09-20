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
/// Loopback-only gallery for completed downloads. Serves HTML cards, Range streams, and thumbs.
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

        Exception? last = null;
        for (var port = PreferredPort; port < PreferredPort + 20; port++)
        {
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                _listener = listener;
                BaseUri = new Uri(prefix);
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
                _logger.LogInformation("Local library listening at {Url}", prefix);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException(
            "Unable to bind local library on 127.0.0.1.", last);
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

                await WriteJsonAsync(ctx, _kinds.GetAll().Select(x => new { value = x.Value, label = x.Label }));
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
            var entry = await _kinds.AddAsync(req?.Label ?? string.Empty, CancellationToken.None);
            await WriteJsonAsync(ctx, new { value = entry.Value, label = entry.Label });
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
            await _engine.UpdateLibraryItemAsync(
                id,
                req.TitleHead,
                req.Caption,
                req.VideoKind,
                CancellationToken.None);

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

    private async Task WriteVideosJsonAsync(HttpListenerContext ctx)
    {
        var groupMode = (ctx.Request.QueryString["group"] ?? "").Trim().ToLowerInvariant();
        var items = (await LoadCompletedAsync()).ToList();
        var timeOrdered = items.OrderByDescending(i => i.DownloadedAt).ToList();

        object payload = groupMode switch
        {
            "site" => items.GroupBy(i => i.SiteGroup)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    group = g.Key,
                    items = LocalLibraryOrdering
                        .OrderByAuthorThenDownloadedAtDesc(g, i => i.Author, i => i.DownloadedAt)
                        .ToArray()
                })
                .ToArray(),
            "kind" => items.GroupBy(i => i.VideoKindLabel)
                .OrderBy(g => g.Key == "未分类" ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    group = g.Key,
                    items = LocalLibraryOrdering
                        .OrderByAuthorThenDownloadedAtDesc(g, i => i.Author, i => i.DownloadedAt)
                        .ToArray()
                })
                .ToArray(),
            _ => timeOrdered
        };

        await WriteJsonAsync(ctx, payload);
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

        await WriteJsonAsync(ctx, item);
    }

    private async Task WriteThumbAsync(HttpListenerContext ctx, Guid id)
    {
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

        var thumb = _thumbs.GetPath(id);
        if (!_thumbs.Exists(id))
        {
            _thumbs.EnsureAsyncFireAndForget(id, job.TargetPath);
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentType = "image/svg+xml";
            var svg = Encoding.UTF8.GetBytes(
                "<svg xmlns='http://www.w3.org/2000/svg' width='320' height='200'><rect fill='#1f2937' width='100%' height='100%'/><text x='50%' y='50%' fill='#9ca3af' font-size='14' text-anchor='middle' dy='.3em'>…</text></svg>");
            ctx.Response.ContentLength64 = svg.Length;
            await ctx.Response.OutputStream.WriteAsync(svg);
            return;
        }

        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        await using var fs = File.OpenRead(thumb);
        ctx.Response.ContentLength64 = fs.Length;
        await fs.CopyToAsync(ctx.Response.OutputStream);
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
        var length = new FileInfo(path).Length;
        var contentType = GuessContentType(path);
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.ContentType = contentType;

        long start = 0;
        long end = length - 1;
        var range = ctx.Request.Headers["Range"];
        if (!string.IsNullOrWhiteSpace(range) &&
            range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range[6..];
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var left = spec[..dash];
                var right = spec[(dash + 1)..];
                if (long.TryParse(left, out var s))
                    start = s;
                if (long.TryParse(right, out var e) && e >= start)
                    end = e;
                end = Math.Min(end, length - 1);
                if (start > end || start >= length)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers["Content-Range"] = $"bytes */{length}";
                    return;
                }

                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
            }
        }

        var count = end - start + 1;
        ctx.Response.ContentLength64 = count;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[1024 * 256];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read <= 0)
                break;
            await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    private async Task<List<LibraryItem>> LoadCompletedAsync()
    {
        var all = await _repository.GetAllAsync();
        var list = new List<LibraryItem>();
        foreach (var job in all)
        {
            if (job.Status != DownloadStatus.Completed)
                continue;
            if (string.IsNullOrWhiteSpace(job.TargetPath) || !File.Exists(job.TargetPath))
                continue;

            var audioOnly = IsAudioOnlyJob(job);
            if (!audioOnly)
                _thumbs.EnsureAsyncFireAndForget(job.Id, job.TargetPath);
            var fileName = Path.GetFileName(job.TargetPath);
            var stem = Path.GetFileNameWithoutExtension(job.TargetPath);
            DownloadFileNameBuilder.TrySplitMetaSuffix(stem, out var titleHead, out var metaSuffix);
            if (string.IsNullOrWhiteSpace(titleHead))
                titleHead = stem;
            var caption = string.IsNullOrWhiteSpace(job.Caption) ? job.DisplayName : job.Caption!;
            var kind = _kinds.Normalize(job.VideoKind);
            var downloadedAt = job.CompletedAt ?? job.UpdatedAt;
            list.Add(new LibraryItem(
                job.Id,
                fileName,
                titleHead,
                metaSuffix,
                Path.GetExtension(job.TargetPath),
                caption,
                kind,
                _kinds.LabelOf(kind),
                downloadedAt,
                job.Author,
                DownloadSiteFolder.Resolve(job.PageUrl),
                $"/api/thumb/{job.Id:N}",
                $"/api/stream/{job.Id:N}",
                $"/play/{job.Id:N}",
                audioOnly));
        }

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
        bool IsAudioOnly)
    {
        public string DownloadedAtText =>
            DownloadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        public string AuthorDisplay =>
            "作者：" + AuthorNameResolver.DisplayOrUnknown(Author);
    }
}
