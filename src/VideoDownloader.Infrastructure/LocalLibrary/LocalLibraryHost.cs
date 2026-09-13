using System.Collections.Concurrent;
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
    private readonly IFfmpegAdapter _ffmpeg;
    private readonly ILogger<LocalLibraryHost> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _thumbQueued = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly string _thumbRoot;

    public LocalLibraryHost(
        IDownloadRepository repository,
        IFfmpegAdapter ffmpeg,
        ILogger<LocalLibraryHost> logger)
    {
        _repository = repository;
        _ffmpeg = ffmpeg;
        _logger = logger;
        _thumbRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "thumbs");
        Directory.CreateDirectory(_thumbRoot);
    }

    public Uri? BaseUri { get; private set; }
    public bool IsRunning => _listener?.IsListening == true;

    public static bool IsLocalLibraryHost(Uri? uri)
    {
        if (uri is null || uri.Scheme is not ("http" or "https"))
            return false;
        return uri.Host is "127.0.0.1" or "localhost" or "::1";
    }

    public string GalleryUrl => (BaseUri ?? new Uri($"http://127.0.0.1:{PreferredPort}/")).AbsoluteUri;

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
                await WriteHtmlAsync(ctx, LocalLibraryPages.PlayerHtml);
                return;
            }

            if (path.Equals("/api/videos", StringComparison.OrdinalIgnoreCase))
            {
                await WriteVideosJsonAsync(ctx);
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

    private async Task WriteVideosJsonAsync(HttpListenerContext ctx)
    {
        var group = string.Equals(ctx.Request.QueryString["group"], "site", StringComparison.OrdinalIgnoreCase);
        var items = await LoadCompletedAsync();
        items = items
            .OrderByDescending(i => i.DownloadedAt)
            .ToList();

        object payload = group
            ? items.GroupBy(i => i.Group)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    group = g.Key,
                    items = g.OrderByDescending(i => i.DownloadedAt).ToArray()
                })
                .ToArray()
            : items;

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

        var thumb = ThumbPath(id);
        if (!File.Exists(thumb))
        {
            QueueThumb(job);
            // Tiny SVG placeholder
            ctx.Response.ContentType = "image/svg+xml";
            var svg = Encoding.UTF8.GetBytes(
                "<svg xmlns='http://www.w3.org/2000/svg' width='320' height='180'><rect fill='#1f2937' width='100%' height='100%'/><text x='50%' y='50%' fill='#9ca3af' font-size='14' text-anchor='middle' dy='.3em'>…</text></svg>");
            ctx.Response.ContentLength64 = svg.Length;
            await ctx.Response.OutputStream.WriteAsync(svg);
            return;
        }

        ctx.Response.ContentType = "image/jpeg";
        await using var fs = File.OpenRead(thumb);
        ctx.Response.ContentLength64 = fs.Length;
        await fs.CopyToAsync(ctx.Response.OutputStream);
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

            QueueThumb(job);
            var fileName = Path.GetFileName(job.TargetPath);
            var caption = string.IsNullOrWhiteSpace(job.Caption) ? job.DisplayName : job.Caption!;
            list.Add(new LibraryItem(
                job.Id,
                fileName,
                caption,
                job.UpdatedAt,
                DownloadSiteFolder.Resolve(job.PageUrl),
                $"/api/thumb/{job.Id:N}",
                $"/api/stream/{job.Id:N}",
                $"/play/{job.Id:N}"));
        }

        return list;
    }

    private void QueueThumb(DownloadJob job)
    {
        var thumb = ThumbPath(job.Id);
        if (File.Exists(thumb))
            return;
        if (!_thumbQueued.TryAdd(job.Id, 0))
            return;

        var path = job.TargetPath;
        var id = job.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await _ffmpeg.TryExtractThumbnailAsync(path, thumb, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumb extract failed for {JobId}", id);
            }
            finally
            {
                _thumbQueued.TryRemove(id, out _);
            }
        });
    }

    private string ThumbPath(Guid id) => Path.Combine(_thumbRoot, id.ToString("N") + ".jpg");

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

    private sealed record LibraryItem(
        Guid Id,
        string FileName,
        string Caption,
        DateTimeOffset DownloadedAt,
        string Group,
        string ThumbUrl,
        string StreamUrl,
        string PlayUrl)
    {
        public string DownloadedAtText =>
            DownloadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
