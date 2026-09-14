using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;
using VideoDownloader.Infrastructure.Detection.Sites.TikTok;
using VideoDownloader.Infrastructure.Logging;
using VideoDownloader.Infrastructure.Licensing;

namespace VideoDownloader.Infrastructure.Http;

public sealed class HttpMediaDownloader
{
    private readonly HttpClient _client;
    private readonly IRequestMessageFactory _requestFactory;
    private readonly AppOptions _options;
    private readonly ILogger<HttpMediaDownloader> _logger;
    private readonly HttpClient? _ipv4FallbackClient;
    private readonly LicenseService? _license;

    public HttpMediaDownloader(
        HttpClient client,
        IRequestMessageFactory requestFactory,
        IOptions<AppOptions> options,
        ILogger<HttpMediaDownloader> logger)
        : this(client, null, requestFactory, options, logger, null)
    {
    }

    public HttpMediaDownloader(
        HttpClient client,
        HttpClient? ipv4FallbackClient,
        IRequestMessageFactory requestFactory,
        IOptions<AppOptions> options,
        ILogger<HttpMediaDownloader> logger,
        LicenseService? license = null)
    {
        _client = client;
        _requestFactory = requestFactory;
        _options = options.Value;
        _logger = logger;
        _ipv4FallbackClient = ipv4FallbackClient;
        _license = license;
    }

    public async Task DownloadDirectAsync(
        DownloadJob job,
        IProgress<long>? progress,
        Func<Task>? checkpointAsync,
        CancellationToken ct)
    {
        if (TryGetHttpDashFragmentCount(job) is int fragCount)
        {
            await DownloadHttpDashFragmentsAsync(job, fragCount, progress, checkpointAsync, ct);
            return;
        }

        if (LooksLikeYoutubeHangUrl(job.Variant.SourceUrl))
        {
            // Force the existing HTTP_403 recovery path to re-resolve via yt-dlp so
            // HttpDashFragmentCount is attached; do not leave the job permanently failed.
            throw new DownloadException(
                ErrorCodes.Http403,
                "YouTube hang/DASH segment URL is missing fragment metadata; renewing.");
        }

        var maxAttempts = Math.Max(1, _options.Download.RetryCount + 1);
        Exception? lastError = null;
        var lastPartBytes = PartLength(job);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadDirectCoreAsync(job, progress, checkpointAsync, ct);
                return;
            }
            catch (HttpRequestException ex) when (_ipv4FallbackClient is not null && IsConnectivityFailure(ex))
            {
                _logger.LogWarning(
                    "System network connection failed for {Host}; retrying with direct IPv4 fallback",
                    job.Variant.SourceUrl.Host);

                try
                {
                    await DownloadDirectCoreAsync(job, progress, checkpointAsync, ct, _ipv4FallbackClient);
                    _logger.LogInformation("Direct IPv4 fallback succeeded for {Host}", job.Variant.SourceUrl.Host);
                    return;
                }
                catch (HttpRequestException fallbackEx)
                {
                    lastError = fallbackEx;
                    _logger.LogWarning(
                        "Direct IPv4 fallback also failed for {Host}: {ErrorType}",
                        job.Variant.SourceUrl.Host,
                        fallbackEx.InnerException?.GetType().Name ?? fallbackEx.GetType().Name);
                    await DelayRetryAsync(attempt, ct);
                }
            }
            catch (DownloadException ex) when (
                ex.ErrorCode is ErrorCodes.RangeMismatch or ErrorCodes.Range416 &&
                attempt < maxAttempts)
            {
                ResetPart(job);
                lastError = ex;
                lastPartBytes = 0;
                _logger.LogWarning(
                    "Range resume reset for {JobId}, attempt {Attempt}: {Reason}",
                    job.Id,
                    attempt,
                    SanitizedLogger.SanitizeMessage(ex.Message));
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                throw new DownloadException(ErrorCodes.DiskFull, "Disk full.");
            }
            catch (IOException ex) when (
                IsTransientTransport(ex) &&
                BilibiliCdnPreference.IsMediaHost(job.Variant.SourceUrl))
            {
                lastError = ex;
                if (!TryContinueBilibiliTransport(job, ex, ref lastPartBytes, ref attempt, maxAttempts))
                    throw;
                await DelayRetryAsync(Math.Max(1, attempt), ct);
            }
            catch (DownloadException ex) when (
                ex.ErrorCode == ErrorCodes.IncompleteDownload &&
                BilibiliCdnPreference.IsMediaHost(job.Variant.SourceUrl))
            {
                lastError = ex;
                if (!TryContinueBilibiliTransport(job, ex, ref lastPartBytes, ref attempt, maxAttempts))
                    throw;
                await DelayRetryAsync(Math.Max(1, attempt), ct);
            }
            catch (HttpRequestException ex) when (
                attempt < maxAttempts ||
                BilibiliCdnPreference.IsMediaHost(job.Variant.SourceUrl))
            {
                lastError = ex;
                if (BilibiliCdnPreference.IsMediaHost(job.Variant.SourceUrl))
                {
                    if (!TryContinueBilibiliTransport(job, ex, ref lastPartBytes, ref attempt, maxAttempts))
                        throw;
                    await DelayRetryAsync(Math.Max(1, attempt), ct);
                    continue;
                }

                await DelayRetryAsync(attempt, ct);
            }
        }

        throw lastError ?? new DownloadException(ErrorCodes.NetTimeout, "Download failed after retries.");
    }

    private async Task DownloadDirectCoreAsync(
        DownloadJob job,
        IProgress<long>? progress,
        Func<Task>? checkpointAsync,
        CancellationToken ct,
        HttpClient? client = null)
    {
        var variant = job.Variant;
        var softMedia = IsSoftMediaJob(job);
        var partPath = job.TargetPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);

        var offset = 0L;
        // Douyin album/BGM CDNs frequently break or truncate when Range is used — always full GET.
        if (softMedia)
        {
            if (File.Exists(partPath))
                File.Delete(partPath);
            if (File.Exists(job.TargetPath))
                File.Delete(job.TargetPath);
            ClearChunkDir(partPath);
            job.TotalBytes = null;
            job.DownloadedBytes = 0;
        }
        else if (File.Exists(partPath))
        {
            offset = new FileInfo(partPath).Length;
            job.DownloadedBytes = offset;
        }

        job.DownloadedBytes = offset;

        // YouTube/googlevideo: multi-connection Range when starting fresh (or resuming chunk dir).
        // Keep single-stream resume when a legacy contiguous .part already exists.
        if (!softMedia &&
            TryBeginParallelRange(job, partPath, offset, out var parallelTotal))
        {
            var ranges = SplitByteRanges(parallelTotal, _options.Download.ParallelConnections);
            if (ranges.Length > 1)
            {
                try
                {
                    await DownloadParallelRangesAsync(
                        job, partPath, parallelTotal, ranges, progress, checkpointAsync, ct, client);
                    return;
                }
                catch (DownloadException ex) when (
                    ex.ErrorCode is ErrorCodes.RangeMismatch or ErrorCodes.Range416 or
                        ErrorCodes.IncompleteDownload or ErrorCodes.Http403)
                {
                    _logger.LogWarning(
                        "Parallel Range download failed for {JobId} ({Code}); falling back to single connection",
                        job.Id,
                        ex.ErrorCode);
                    ClearChunkDir(partPath);
                    if (File.Exists(partPath))
                        File.Delete(partPath);
                    offset = 0;
                    job.DownloadedBytes = 0;
                }
            }
        }

        var resource = BuildResource(variant, job);
        var url = variant.SourceUrl;

        var redirects = 0;
        while (true)
        {
            using var request = _requestFactory.Create(
                resource with { Url = url },
                HttpMethod.Get);

            if (!softMedia)
            {
                // TikTok signed progressive CDNs 403 on `Range: bytes=0-`. Other sites keep Range.
                var skipTikTokInitialRange = offset == 0 &&
                    TikTokCdn.IsSignedProgressiveHost(job.Variant.SourceUrl);
                if (skipTikTokInitialRange)
                {
                    request.Headers.Range = null;
                    request.Headers.Remove("Range");
                    request.Headers.Remove("If-Range");
                }
                else
                {
                    request.Headers.Range = new RangeHeaderValue(offset,
                        IsParallelRangeHost(url)
                            ? Math.Max(offset, Math.Min(offset + 1024 * 1024 - 1, (job.TotalBytes ?? long.MaxValue) - 1))
                            : null);
                    request.Headers.Remove("If-Range");
                    if (offset > 0)
                        RequestMessageFactory.ApplyIfRange(request, job.ETag, job.LastModified);
                }
            }

            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.ParseAdd("identity");

            _logger.LogInformation(
                "GET {Url} offset={Offset}",
                SanitizedLogger.SanitizeUrl(url.ToString()),
                offset);

            using var response = await (client ?? _client).SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (IsRedirect(response.StatusCode))
            {
                var next = ResolveRedirectUrl(url, response.Headers.Location);
                if (next is null || next == url || ++redirects > 8)
                    throw new DownloadException(ErrorCodes.NetTimeout, "Too many redirects.");
                url = next;
                continue;
            }

            if (IsRetriableStatus(response.StatusCode))
                throw new HttpRequestException($"Retriable status {(int)response.StatusCode}");

            _logger.LogInformation("Download response job={JobId} status={Status} offset={Offset} range={Range} length={Length}",
                job.Id, (int)response.StatusCode, offset, response.Content.Headers.ContentRange?.ToString(),
                response.Content.Headers.ContentLength);
            var previousOffset = offset;
            await using (var file = new FileStream(
                             partPath,
                             FileMode.OpenOrCreate,
                             FileAccess.ReadWrite,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (file.Length != offset)
                {
                    file.SetLength(offset > 0 ? offset : 0);
                    file.Position = offset;
                }

                offset = await HandleResponseAsync(job, file, offset, response, progress, checkpointAsync, ct);
                await file.FlushAsync(ct);
            }

            // Stream is closed: reconcile .part if the job was renamed mid-transfer.
            partPath = ReconcilePartPath(job, partPath);
            if (!softMedia &&
                response.StatusCode == HttpStatusCode.PartialContent &&
                job.TotalBytes is long total && offset < total)
            {
                if (offset <= previousOffset)
                    throw new DownloadException(ErrorCodes.IncompleteDownload, "Partial response made no progress.");
                redirects = 0;
                continue;
            }

            // Soft-media full GET: accept whatever body arrived if it looks like a real object.
            if (softMedia && offset > 0 && offset < 8 * 1024 &&
                response.Content.Headers.ContentLength is long declared && declared > offset)
            {
                // Tiny body vs large declared length — still keep if we got a usable image/audio crumb.
                if (offset < 1024)
                    throw new DownloadException(ErrorCodes.IncompleteDownload,
                        $"Downloaded {offset} of {declared} bytes.");
                job.TotalBytes = offset;
            }

            EnsureDownloadLooksComplete(job, offset);
            await FinalizeDownloadAsync(job, partPath, ct);
            return;
        }
    }

    private bool TryBeginParallelRange(DownloadJob job, string partPath, long existingOffset, out long totalBytes)
    {
        totalBytes = 0;
        var opts = _options.Download;
        if (opts.ParallelConnections <= 1)
            return false;
        if (TryGetHttpDashFragmentCount(job) is not null)
            return false;
        if (LooksLikeYoutubeHangUrl(job.Variant.SourceUrl))
            return false;
        if (!IsParallelRangeHost(job.Variant.SourceUrl))
            return false;
        if (TikTokCdn.IsSignedProgressiveHost(job.Variant.SourceUrl))
            return false;

        var chunkDir = ChunkDir(partPath);
        var hasChunks = Directory.Exists(chunkDir) &&
                        Directory.EnumerateFiles(chunkDir).Any();

        // Contiguous single-stream .part already present — do not rewrite with parallel chunks.
        if (existingOffset > 0 && !hasChunks)
            return false;

        if (job.TotalBytes is long known && known >= opts.ParallelMinBytes)
        {
            totalBytes = known;
            return true;
        }

        // Resume a previous parallel attempt even if TotalBytes was cleared from the job row.
        if (hasChunks && TryReadChunkManifest(chunkDir, out var manifestTotal) &&
            manifestTotal >= opts.ParallelMinBytes)
        {
            totalBytes = manifestTotal;
            job.TotalBytes = manifestTotal;
            return true;
        }

        return false;
    }

    internal static bool IsParallelRangeHost(Uri url)
    {
        var host = url.Host;
        return host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Album/BGM style objects that often break on Range. YouTube audio is excluded — it Range-resumes well.
    /// </summary>
    private static bool IsSoftMediaJob(DownloadJob job) =>
        job.Variant.Tracks.Count > 0 &&
        job.Variant.Tracks.All(t => t.Kind is MediaTrackKind.Image or MediaTrackKind.Audio) &&
        !IsParallelRangeHost(job.Variant.SourceUrl);

    /// <summary>Splits [0, total) into up to <paramref name="connections"/> inclusive byte ranges.</summary>
    internal static (long Start, long End)[] SplitByteRanges(long totalBytes, int connections)
    {
        if (totalBytes <= 0)
            return [];
        connections = Math.Clamp(connections, 1, 32);
        // Keep chunks reasonably large so Range overhead stays low.
        var maxBySize = (int)Math.Max(1, totalBytes / (256L * 1024));
        connections = Math.Min(connections, maxBySize);
        var chunk = totalBytes / connections;
        var ranges = new (long Start, long End)[connections];
        long cursor = 0;
        for (var i = 0; i < connections; i++)
        {
            var start = cursor;
            var end = i == connections - 1 ? totalBytes - 1 : cursor + chunk - 1;
            ranges[i] = (start, end);
            cursor = end + 1;
        }

        return ranges;
    }

    private async Task DownloadParallelRangesAsync(
        DownloadJob job,
        string partPath,
        long totalBytes,
        (long Start, long End)[] ranges,
        IProgress<long>? progress,
        Func<Task>? checkpointAsync,
        CancellationToken ct,
        HttpClient? client)
    {
        if (_license?.DownloadLimitBytes is int demoLimit && totalBytes > demoLimit)
            throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");

        var chunkDir = ChunkDir(partPath);
        Directory.CreateDirectory(chunkDir);
        WriteChunkManifest(chunkDir, totalBytes, ranges.Length);

        job.TotalBytes = totalBytes;
        if (job.ExpectedTotalBytes is not > 0)
            job.ExpectedTotalBytes = totalBytes;
        var http = client ?? _client;
        var resource = BuildResource(job.Variant, job);
        var url = job.Variant.SourceUrl;

        long sharedBytes = 0;
        for (var i = 0; i < ranges.Length; i++)
        {
            var path = ChunkPath(chunkDir, i);
            if (File.Exists(path))
                sharedBytes += new FileInfo(path).Length;
        }

        job.DownloadedBytes = sharedBytes;
        progress?.Report(sharedBytes);
        if (checkpointAsync is not null)
            await checkpointAsync();

        _logger.LogInformation(
            "Parallel Range download job={JobId} connections={Connections} total={Total} host={Host}",
            job.Id,
            ranges.Length,
            totalBytes,
            url.Host);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressGate = new object();
        var speedClock = System.Diagnostics.Stopwatch.StartNew();
        var lastLoggedBytes = sharedBytes;
        var tasks = new Task[ranges.Length];
        for (var i = 0; i < ranges.Length; i++)
        {
            var index = i;
            var (start, end) = ranges[i];
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    await DownloadOneRangeChunkAsync(
                        job,
                        resource,
                        url,
                        http,
                        chunkDir,
                        index,
                        start,
                        end,
                        () =>
                        {
                            lock (progressGate)
                            {
                                var total = Interlocked.Read(ref sharedBytes);
                                job.DownloadedBytes = total;
                                progress?.Report(total);
                                if (speedClock.Elapsed.TotalSeconds >= 10)
                                {
                                    _logger.LogInformation("HTTP transfer job={JobId} mode=parallel host={Host} bytes={Bytes} KiBps={Speed:F1}",
                                        job.Id, url.Host, total, (total - lastLoggedBytes) / speedClock.Elapsed.TotalSeconds / 1024);
                                    lastLoggedBytes = total;
                                    speedClock.Restart();
                                }
                            }
                        },
                        bytes => Interlocked.Add(ref sharedBytes, bytes),
                        stop.Token);
                }
                catch
                {
                    stop.Cancel();
                    throw;
                }
            }, ct);
        }

        await Task.WhenAll(tasks);

        job.DownloadedBytes = totalBytes;
        progress?.Report(totalBytes);
        if (checkpointAsync is not null)
            await checkpointAsync();

        await AssembleChunksAsync(partPath, chunkDir, ranges, ct);
        ClearChunkDir(partPath);
        EnsureDownloadLooksComplete(job, totalBytes);
        await FinalizeDownloadAsync(job, partPath, ct);
    }

    private async Task DownloadOneRangeChunkAsync(
        DownloadJob job,
        MediaResource resource,
        Uri url,
        HttpClient client,
        string chunkDir,
        int index,
        long rangeStart,
        long rangeEnd,
        Action publishProgress,
        Action<long> addBytes,
        CancellationToken ct)
    {
        var expected = rangeEnd - rangeStart + 1;
        var chunkPath = ChunkPath(chunkDir, index);
        var have = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0L;
        if (have > expected)
        {
            File.Delete(chunkPath);
            have = 0;
        }

        if (have == expected)
            return;

        await using var file = new FileStream(
            chunkPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != have)
            file.SetLength(have);
        file.Position = have;

        // Keep the on-disk partition layout for Resume, but use small HTTP requests:
        // large googlevideo ranges are throttled even with multiple connections.
        var buffer = new byte[64 * 1024];
        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        var failures = 0;
        while (have < expected)
        {
            var from = rangeStart + have;
            var to = Math.Min(rangeEnd, from + 1024 * 1024 - 1);
            try
            {
                using var response = await SendRangeAsync(resource, url, client, from, to, ct);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new DownloadException(ErrorCodes.Http403, "Access denied (403).");
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new DownloadException(ErrorCodes.Range416, "Range not satisfiable.");
                if (IsRetriableStatus(response.StatusCode))
                    throw new HttpRequestException($"Retriable status {(int)response.StatusCode}");
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent ||
                    range?.From != from || range.To != to || range.Length != job.TotalBytes)
                    throw new DownloadException(ErrorCodes.RangeMismatch, "Parallel chunk Content-Range mismatch.");

                await using var input = await response.Content.ReadAsStreamAsync(ct);
                int read;
                long received = 0;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (received + read > to - from + 1)
                        throw new DownloadException(ErrorCodes.RangeMismatch, "Parallel response exceeds requested range.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    have += read;
                    received += read;
                    addBytes(read);
                    if (progressClock.ElapsedMilliseconds >= 250)
                    {
                        publishProgress();
                        progressClock.Restart();
                    }
                }
                if (received != to - from + 1)
                    throw new HttpRequestException("Incomplete parallel range response.");
                failures = 0;
                publishProgress();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                (ex is HttpRequestException || ex is IOException && IsTransientTransport(ex)) &&
                failures < _options.Download.RetryCount)
            {
                failures++;
                _logger.LogWarning("Range transport retry job={JobId} chunk={Chunk} offset={Offset} attempt={Attempt}",
                    job.Id, index, rangeStart + have, failures);
                await file.FlushAsync(ct);
                publishProgress();
                await DelayRetryAsync(failures, ct);
            }
        }

        await file.FlushAsync(ct);
        publishProgress();

        if (file.Length != expected)
            throw new DownloadException(
                ErrorCodes.IncompleteDownload,
                $"Parallel chunk {index} got {file.Length} of {expected} bytes.");
    }

    private async Task<HttpResponseMessage> SendRangeAsync(
        MediaResource resource, Uri url, HttpClient client, long from, long to, CancellationToken ct)
    {
        for (var redirects = 0; ; redirects++)
        {
            using var request = _requestFactory.Create(resource with { Url = url }, HttpMethod.Get);
            request.Headers.Range = new RangeHeaderValue(from, to);
            request.Headers.Remove("If-Range");
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.ParseAdd("identity");

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRedirect(response.StatusCode))
                return response;

            // The shared client disables automatic redirects. Preserve the exact byte
            // range and rebuild host-scoped cookies for every CDN hop, as in single GET.
            Uri? next;
            using (response)
                next = ResolveRedirectUrl(url, response.Headers.Location);
            if (next is null || next == url || redirects >= 8)
                throw new DownloadException(ErrorCodes.NetTimeout, "Too many redirects.");
            url = next;
        }
    }

    private static async Task AssembleChunksAsync(
        string partPath,
        string chunkDir,
        (long Start, long End)[] ranges,
        CancellationToken ct)
    {
        if (File.Exists(partPath))
            File.Delete(partPath);

        await using var output = new FileStream(
            partPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        for (var i = 0; i < ranges.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunkPath = ChunkPath(chunkDir, i);
            await using var input = new FileStream(
                chunkPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 1024 * 1024, ct);
        }

        await output.FlushAsync(ct);
    }

    private static string ChunkDir(string partPath) => partPath + ".chunks";

    private static string ChunkPath(string chunkDir, int index) =>
        Path.Combine(chunkDir, $"{index:D3}");

    private static void WriteChunkManifest(string chunkDir, long totalBytes, int connections)
    {
        var path = Path.Combine(chunkDir, "manifest.txt");
        File.WriteAllText(path, $"{totalBytes}\n{connections}\n");
    }

    private static bool TryReadChunkManifest(string chunkDir, out long totalBytes)
    {
        totalBytes = 0;
        var path = Path.Combine(chunkDir, "manifest.txt");
        if (!File.Exists(path))
            return false;
        try
        {
            var line = File.ReadLines(path).FirstOrDefault();
            return long.TryParse(line, out totalBytes) && totalBytes > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearChunkDir(string partPath)
    {
        var dir = ChunkDir(partPath);
        if (!Directory.Exists(dir))
            return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private async Task<long> HandleResponseAsync(
        DownloadJob job,
        FileStream file,
        long offset,
        HttpResponseMessage response,
        IProgress<long>? progress,
        Func<Task>? checkpointAsync,
        CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new DownloadException(ErrorCodes.Http403, "Access denied (403).");

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new DownloadException(ErrorCodes.Http404, "Resource not found (404).");

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (!ValidateContentRange(response.Content.Headers.ContentRange, offset))
            {
                ResetPart(job, file);
                throw new DownloadException(ErrorCodes.RangeMismatch, "Content-Range mismatch.");
            }

            file.Position = offset;
        }
        else if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // Server ignored Range (or If-Range failed) — must restart; partial bytes are not trustworthy.
            _logger.LogWarning(
                "Server ignored Range (HTTP 200) for {JobId}; restarting from zero",
                job.Id);
            ResetPart(job, file);
            offset = 0;
            job.DownloadedBytes = 0;
        }
        else if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (IsAlreadyComplete(response, file.Length))
            {
                job.DownloadedBytes = file.Length;
                job.TotalBytes = file.Length;
                return file.Length;
            }

            ResetPart(job, file);
            throw new DownloadException(ErrorCodes.Range416, "Range not satisfiable.");
        }

        response.EnsureSuccessStatusCode();

        var newEtag = response.Headers.ETag?.Tag;
        var newModified = response.Content.Headers.LastModified?.ToString("R");
        var responseTotal = InferTotalBytes(response, offset);

        // Length is the hard identity check for resume. ETag/Last-Modified on signed CDNs often
        // rotate even when the object bytes are unchanged. Bilibili upos totals jitter by a few KB
        // for the same m4s; keep an already-aligned 206 instead of discarding the prefix.
        if (offset > 0 && responseTotal is long known && job.TotalBytes is long prior && known != prior)
        {
            var alignedPartial = response.StatusCode == HttpStatusCode.PartialContent &&
                                 ValidateContentRange(response.Content.Headers.ContentRange, offset);
            if (alignedPartial &&
                BilibiliCdnPreference.CanKeepAlignedResume(job.Variant.SourceUrl, prior, known))
            {
                _logger.LogInformation(
                    "Bilibili keeping aligned 206 resume for {JobId} despite length drift {Prior} -> {Known}",
                    job.Id,
                    prior,
                    known);
            }
            else
            {
                ResetPart(job, file);
                throw new DownloadException(
                    ErrorCodes.RangeMismatch,
                    $"Entity length changed ({prior} -> {known}).");
            }
        }

        var etagDrift = !string.IsNullOrWhiteSpace(job.ETag) && newEtag is not null &&
                        !string.Equals(job.ETag, newEtag, StringComparison.OrdinalIgnoreCase);
        var modifiedDrift = !string.IsNullOrWhiteSpace(job.LastModified) && newModified is not null &&
                            !string.Equals(job.LastModified, newModified, StringComparison.OrdinalIgnoreCase);
        if (offset > 0 && (etagDrift || modifiedDrift))
        {
            var alignedPartial = response.StatusCode == HttpStatusCode.PartialContent &&
                                 ValidateContentRange(response.Content.Headers.ContentRange, offset) &&
                                 (job.TotalBytes is null ||
                                  responseTotal == job.TotalBytes ||
                                  (responseTotal is long knownTotal &&
                                   job.TotalBytes is long priorTotal &&
                                   BilibiliCdnPreference.CanKeepAlignedResume(job.Variant.SourceUrl, priorTotal, knownTotal)));
            if (!alignedPartial)
            {
                ResetPart(job, file);
                job.DownloadedBytes = 0;
                job.ETag = newEtag;
                job.LastModified = newModified;
                throw new DownloadException(ErrorCodes.RangeMismatch, "Entity version changed.");
            }

            _logger.LogInformation(
                "Keeping aligned 206 resume for {JobId} despite validator drift (etag={EtagDrift}, modified={ModifiedDrift})",
                job.Id,
                etagDrift,
                modifiedDrift);
        }

        job.ETag = newEtag ?? job.ETag;
        job.LastModified = newModified ?? job.LastModified;
        job.TotalBytes = responseTotal ?? job.TotalBytes;
        if (job.ExpectedTotalBytes is not > 0 && job.TotalBytes is > 0)
            job.ExpectedTotalBytes = job.TotalBytes;
        if (_license?.DownloadLimitBytes is int demoLimit && job.TotalBytes is long total && total > demoLimit)
            throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");

        if (checkpointAsync is not null)
            await checkpointAsync();

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1024 * 1024];
        var written = offset;
        var speedClock = System.Diagnostics.Stopwatch.StartNew();
        var lastLoggedBytes = written;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (_license?.DownloadLimitBytes is int streamLimit && written + read > streamLimit)
            {
                file.SetLength(0);
                throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");
            }
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            job.DownloadedBytes = written;
            progress?.Report(written);

            if (speedClock.Elapsed.TotalSeconds >= 10)
            {
                _logger.LogInformation("HTTP transfer job={JobId} mode=single host={Host} bytes={Bytes} KiBps={Speed:F1}",
                    job.Id, response.RequestMessage?.RequestUri?.Host ?? job.Variant.SourceUrl.Host,
                    written, (written - lastLoggedBytes) / speedClock.Elapsed.TotalSeconds / 1024);
                lastLoggedBytes = written;
                speedClock.Restart();
            }

            if (written % (4 * 1024 * 1024) < read && checkpointAsync is not null)
                await checkpointAsync();
        }

        var received = written - offset;
        var softMedia = IsSoftMediaJob(job);
        var tiktokSigned = TikTokCdn.IsSignedProgressiveHost(job.Variant.SourceUrl);
        if ((response.Content.Headers.ContentLength is long bodyLength && received != bodyLength) ||
            (response.StatusCode == HttpStatusCode.PartialContent &&
             written != response.Content.Headers.ContentRange!.To!.Value + 1))
        {
            // Douyin/TikTok album CDNs often advertise inflated Content-Length then close early.
            // Accept a non-empty image/audio body rather than failing the whole slideshow.
            // TikTok signed progressive hosts also RST after a usable MP4 body.
            if (!((softMedia && received >= 1024) ||
                  (tiktokSigned && received >= 256L * 1024)))
                throw new DownloadException(ErrorCodes.IncompleteDownload, "Response body length does not match its range.");
            _logger.LogWarning(
                "Soft-media download length mismatch (got {Got}, declared {Declared}); accepting host={Host}",
                received,
                response.Content.Headers.ContentLength,
                job.Variant.SourceUrl.Host);
            job.TotalBytes = written;
        }
        job.DownloadedBytes = file.Length;
        return file.Length;
    }

    private static void EnsureDownloadLooksComplete(DownloadJob job, long actualLength)
    {
        job.DownloadedBytes = actualLength;
        var softMedia = IsSoftMediaJob(job);
        var tiktokSigned = TikTokCdn.IsSignedProgressiveHost(job.Variant.SourceUrl);
        if (job.TotalBytes is long expected && actualLength != expected)
        {
            if ((softMedia && actualLength >= 1024) ||
                (tiktokSigned && actualLength >= MediaResourceSizeFilter.MinDropdownBytes) ||
                (job.Variant.Tracks.Any(t => t.HttpDashFragmentCount is > 1) &&
                 actualLength >= MediaResourceSizeFilter.MinDropdownBytes))
            {
                job.TotalBytes = actualLength;
            }
            else
            {
                throw new DownloadException(ErrorCodes.IncompleteDownload,
                    $"Downloaded {actualLength} of {expected} bytes.");
            }
        }

        var minBytes = softMedia
            ? 1024L
            : MediaResourceSizeFilter.MinDropdownBytes;
        if (job.DownloadedBytes > 0 && job.DownloadedBytes < minBytes)
        {
            throw new DownloadException(
                ErrorCodes.IncompleteDownload,
                $"Downloaded object is only {job.DownloadedBytes} bytes (below credible media floor).");
        }
    }

    private static MediaResource BuildResource(MediaVariant variant, DownloadJob job) =>
        new(
            Guid.NewGuid(),
            variant.SourceUrl,
            MediaType.DirectVideo,
            null,
            "GET",
            null,
            job.TotalBytes,
            null,
            null,
            variant.SourceUrl,
            null,
            variant.RequestContext,
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

    private static bool IsRetriableStatus(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MultipleChoices
            or HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static Uri? ResolveRedirectUrl(Uri current, Uri? location)
    {
        if (location is null)
            return null;

        return location.IsAbsoluteUri ? location : new Uri(current, location);
    }

    private static long? InferTotalBytes(HttpResponseMessage response, long offset)
    {
        var range = response.Content.Headers.ContentRange;
        if (range?.Length is not null)
            return range.Length.Value;

        if (response.Content.Headers.ContentLength is not null)
            return offset + response.Content.Headers.ContentLength.Value;

        return null;
    }

    private async Task FinalizeDownloadAsync(DownloadJob job, string partPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (job.Variant.Container?.ToLowerInvariant() is "mp4" or "m4a" or "mov")
        {
            if (!Mp4StructureValidator.IsValid(partPath, ct))
            {
                ResetPart(job);
                throw new DownloadException(ErrorCodes.IncompleteDownload, "Invalid MP4 structure; download will restart from zero.");
            }
        }
        var target = job.TargetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
            File.Delete(target);
        File.Move(partPath, target);
        await Task.CompletedTask;
    }

    private static string ReconcilePartPath(DownloadJob job, string writtenPartPath)
    {
        var desired = job.TargetPath + ".part";
        if (string.Equals(writtenPartPath, desired, StringComparison.OrdinalIgnoreCase))
            return File.Exists(desired) ? desired : writtenPartPath;

        if (File.Exists(writtenPartPath))
        {
            if (File.Exists(desired))
                File.Delete(desired);
            File.Move(writtenPartPath, desired);
            return desired;
        }

        return File.Exists(desired) ? desired : writtenPartPath;
    }

    private static void ResetPart(DownloadJob job, FileStream? file = null)
    {
        file?.SetLength(0);
        job.DownloadedBytes = 0;
        job.ETag = null;
        job.LastModified = null;
        job.TotalBytes = null;
        job.ExpectedTotalBytes = null;
        job.ContentPrefixHash = null;

        var partPath = job.TargetPath + ".part";
        if (file is null && File.Exists(partPath))
            File.Delete(partPath);
        ClearChunkDir(partPath);
    }

    private static bool ValidateContentRange(ContentRangeHeaderValue? range, long offset) =>
        range?.From == offset && range.To >= offset && range.Length > range.To;

    private static bool IsAlreadyComplete(HttpResponseMessage response, long localLength)
    {
        var total = response.Content.Headers.ContentRange?.Length;
        return total.HasValue && localLength == total.Value;
    }

    private static long PartLength(DownloadJob job)
    {
        var part = job.TargetPath + ".part";
        if (File.Exists(part))
            return new FileInfo(part).Length;
        var chunkDir = ChunkDir(part);
        if (!Directory.Exists(chunkDir))
            return 0;
        return Directory.EnumerateFiles(chunkDir)
            .Where(f => !f.EndsWith("manifest.txt", StringComparison.OrdinalIgnoreCase))
            .Sum(f => new FileInfo(f).Length);
    }

    private bool TryContinueBilibiliTransport(
        DownloadJob job,
        Exception ex,
        ref long lastPartBytes,
        ref int attempt,
        int maxAttempts)
    {
        var now = PartLength(job);
        var progressed = now > lastPartBytes;
        if (progressed)
        {
            lastPartBytes = now;
            attempt = 0;
        }
        else if (attempt >= maxAttempts)
            return false;

        _logger.LogWarning(
            "Bilibili transport reset for {JobId} after {Bytes} bytes (progressed={Progressed}); Range-retrying. {Reason}",
            job.Id,
            now,
            progressed,
            SanitizedLogger.SanitizeMessage(ex.Message));
        return true;
    }

    private static bool IsTransientTransport(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketError && socketError.SocketErrorCode is
                SocketError.ConnectionReset or
                SocketError.ConnectionAborted or
                SocketError.TimedOut or
                SocketError.Shutdown or
                SocketError.NetworkReset)
                return true;

            if (string.Equals(current.GetType().Name, "HttpIOException", StringComparison.Ordinal))
                return true;

            var message = current.Message;
            if (message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("强迫关闭", StringComparison.Ordinal) ||
                message.Contains("prematurely", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("transport connection", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsDiskFull(IOException ex) =>
        ex.Message.Contains("disk", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("space", StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectivityFailure(HttpRequestException ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketError && socketError.SocketErrorCode is
                SocketError.TimedOut or
                SocketError.HostUnreachable or
                SocketError.NetworkUnreachable or
                SocketError.ConnectionRefused or
                SocketError.ConnectionReset)
                return true;
        }

        return false;
    }

    private static async Task DelayRetryAsync(int attempt, CancellationToken ct)
    {
        var delayMs = (int)Math.Min(10_000, 500 * Math.Pow(2, attempt - 1));
        await Task.Delay(delayMs, ct);
    }

    private static int? TryGetHttpDashFragmentCount(DownloadJob job)
    {
        foreach (var track in job.Variant.Tracks)
        {
            if (track.HttpDashFragmentCount is int n and > 1)
                return n;
        }

        return null;
    }

    private static bool LooksLikeYoutubeHangUrl(Uri url)
    {
        var s = url.Query;
        return s.Contains("hang=1", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("source=yt_live_broadcast", StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadHttpDashFragmentsAsync(
        DownloadJob job,
        int fragmentCount,
        IProgress<long>? progress,
        Func<Task>? checkpointAsync,
        CancellationToken ct)
    {
        var partPath = job.TargetPath + ".part";
        var indexPath = partPath + ".fragidx";
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        ClearChunkDir(partPath);

        var startIndex = 0;
        if (File.Exists(partPath) && File.Exists(indexPath) &&
            int.TryParse(await File.ReadAllTextAsync(indexPath, ct), out var parsed) &&
            parsed >= 0 &&
            parsed < fragmentCount)
        {
            startIndex = parsed;
            job.DownloadedBytes = new FileInfo(partPath).Length;
        }
        else
        {
            if (File.Exists(partPath))
                File.Delete(partPath);
            if (File.Exists(indexPath))
                File.Delete(indexPath);
            if (File.Exists(job.TargetPath))
                File.Delete(job.TargetPath);
            job.DownloadedBytes = 0;
            startIndex = 0;
        }

        if (job.TotalBytes is null or <= 0)
            job.TotalBytes = job.Variant.TotalContentLength;

        _logger.LogInformation(
            "HTTP DASH fragment download job={JobId} fragments={Count} start={Start} host={Host}",
            job.Id,
            fragmentCount,
            startIndex,
            job.Variant.SourceUrl.Host);

        await using (var output = new FileStream(
                         partPath,
                         FileMode.OpenOrCreate,
                         FileAccess.Write,
                         FileShare.Read))
        {
            if (startIndex == 0)
                output.SetLength(0);
            else
                output.Seek(0, SeekOrigin.End);

            const int batchSize = 4;
            var template = job.Variant.SourceUrl;
            var resource = BuildResource(job.Variant, job);

            for (var index = startIndex; index < fragmentCount; index += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batchCount = Math.Min(batchSize, fragmentCount - index);

                var fetchTasks = Enumerable.Range(0, batchCount).Select(async offset =>
                {
                    var fragIndex = index + offset;
                    var bytes = await DownloadOneDashFragmentAsync(
                        resource,
                        WithFragmentSequence(template, fragIndex),
                        ct);
                    return (Offset: offset, Bytes: bytes);
                });

                var results = await Task.WhenAll(fetchTasks);
                foreach (var item in results.OrderBy(x => x.Offset))
                {
                    if (item.Bytes.Length == 0)
                        throw new DownloadException(
                            ErrorCodes.IncompleteDownload,
                            $"Empty DASH fragment {index + item.Offset}.");

                    await output.WriteAsync(item.Bytes, ct);
                    job.DownloadedBytes += item.Bytes.Length;
                    progress?.Report(job.DownloadedBytes);
                }

                var nextIndex = index + batchCount;
                await File.WriteAllTextAsync(
                    indexPath,
                    nextIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ct);

                if (checkpointAsync is not null && (nextIndex % 16 == 0 || nextIndex >= fragmentCount))
                    await checkpointAsync();
            }

            await output.FlushAsync(ct);
        }

        TryDeleteFile(indexPath);
        EnsureDownloadLooksComplete(job, job.DownloadedBytes);
        await FinalizeDownloadAsync(job, partPath, ct);
    }

    private async Task<byte[]> DownloadOneDashFragmentAsync(
        MediaResource resource,
        Uri fragmentUrl,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = _requestFactory.Create(
                    resource with { Url = fragmentUrl },
                    HttpMethod.Get);
                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);

                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    var redirected = ResolveRedirectUrl(fragmentUrl, location);
                    if (redirected is null)
                        throw new DownloadException(ErrorCodes.IncompleteDownload, "Fragment redirect without Location.");
                    fragmentUrl = redirected;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetriableStatus(response.StatusCode) && attempt < 3)
                    {
                        await DelayRetryAsync(attempt, ct);
                        continue;
                    }

                    throw new DownloadException(
                        response.StatusCode == HttpStatusCode.Forbidden ? ErrorCodes.Http403 : ErrorCodes.IncompleteDownload,
                        $"Fragment HTTP {(int)response.StatusCode}");
                }

                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 3)
            {
                last = ex;
                await DelayRetryAsync(attempt, ct);
            }
        }

        throw last ?? new DownloadException(ErrorCodes.NetTimeout, "Fragment download failed.");
    }

    internal static Uri WithFragmentSequence(Uri url, int sequence)
    {
        var text = url.AbsoluteUri;
        const string marker = "sq=";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = idx + marker.Length;
            var end = start;
            while (end < text.Length && char.IsDigit(text[end]))
                end++;
            return new Uri(string.Concat(text.AsSpan(0, start), sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), text.AsSpan(end)));
        }

        var join = text.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return new Uri(text + join + marker + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
