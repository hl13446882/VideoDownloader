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
        var maxAttempts = Math.Max(1, _options.Download.RetryCount + 1);
        Exception? lastError = null;

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
                _logger.LogWarning("Range resume reset for {JobId}, attempt {Attempt}", job.Id, attempt);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                await DelayRetryAsync(attempt, ct);
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                throw new DownloadException(ErrorCodes.DiskFull, "Disk full.");
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
        var partPath = job.TargetPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);

        var offset = 0L;
        if (File.Exists(partPath))
        {
            offset = new FileInfo(partPath).Length;
            job.DownloadedBytes = offset;
        }

        var resource = BuildResource(variant, job);
        var url = variant.SourceUrl;

        for (var redirect = 0; redirect < 8; redirect++)
        {
            using var request = _requestFactory.Create(
                resource with { Url = url },
                HttpMethod.Get);

            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                RequestMessageFactory.ApplyIfRange(request, job.ETag, job.LastModified);
            }

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
                if (next is null || next == url)
                    break;
                url = next;
                continue;
            }

            if (IsRetriableStatus(response.StatusCode))
                throw new HttpRequestException($"Retriable status {(int)response.StatusCode}");

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
            await FinalizeDownloadAsync(job, partPath, ct);
            return;
        }

        throw new DownloadException(ErrorCodes.NetTimeout, "Too many redirects.");
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

        if (offset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
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
            ResetPart(job, file);
            offset = 0;
            job.DownloadedBytes = 0;
        }
        else if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (IsAlreadyComplete(response, file.Length))
            {
                job.DownloadedBytes = file.Length;
                return offset;
            }

            ResetPart(job, file);
            throw new DownloadException(ErrorCodes.Range416, "Range not satisfiable.");
        }

        response.EnsureSuccessStatusCode();

        var newEtag = response.Headers.ETag?.Tag;
        var newModified = response.Content.Headers.LastModified?.ToString("R");

        if (offset > 0 &&
            (!string.IsNullOrWhiteSpace(job.ETag) && newEtag is not null &&
             !string.Equals(job.ETag, newEtag, StringComparison.OrdinalIgnoreCase) ||
             !string.IsNullOrWhiteSpace(job.LastModified) && newModified is not null &&
             !string.Equals(job.LastModified, newModified, StringComparison.OrdinalIgnoreCase)))
        {
            ResetPart(job, file);
            job.DownloadedBytes = 0;
            job.ETag = newEtag;
            job.LastModified = newModified;
            throw new DownloadException(ErrorCodes.RangeMismatch, "Entity version changed.");
        }

        job.ETag = newEtag ?? job.ETag;
        job.LastModified = newModified ?? job.LastModified;
        job.TotalBytes ??= InferTotalBytes(response, offset);
        if (_license?.DownloadLimitBytes is int demoLimit && job.TotalBytes is long total && total > demoLimit)
            throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");

        if (checkpointAsync is not null)
            await checkpointAsync();

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (_license?.DownloadLimitBytes is int streamLimit && job.DownloadedBytes + read > streamLimit)
            {
                file.SetLength(0);
                throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");
            }
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            job.DownloadedBytes += read;
            progress?.Report(job.DownloadedBytes);

            if (job.DownloadedBytes % (4 * 1024 * 1024) < read && checkpointAsync is not null)
                await checkpointAsync();
        }

        // After reading the body: never treat a short 206 window / tiny object as a finished VOD.
        EnsureDownloadLooksComplete(job, response, offset);

        return offset;
    }

    private static void EnsureDownloadLooksComplete(DownloadJob job, HttpResponseMessage response, long startOffset)
    {
        var range = response.Content.Headers.ContentRange;
        if (startOffset == 0 &&
            response.StatusCode == HttpStatusCode.PartialContent &&
            range?.Length is long entity &&
            range.To is long to &&
            to + 1 < entity)
        {
            throw new DownloadException(
                ErrorCodes.IncompleteDownload,
                $"Partial 206 window ended at {to + 1} of {entity} bytes.");
        }

        if (job.TotalBytes is long expected && expected > 0 && job.DownloadedBytes + 1024 < expected)
        {
            throw new DownloadException(
                ErrorCodes.IncompleteDownload,
                $"Downloaded {job.DownloadedBytes} of {expected} bytes.");
        }

        var minBytes = job.Variant.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
            ? MediaResourceSizeFilter.MinProgressiveVideoBytes
            : MediaResourceSizeFilter.MinDisplayBytes;
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

        var partPath = job.TargetPath + ".part";
        if (file is null && File.Exists(partPath))
            File.Delete(partPath);
    }

    private static bool ValidateContentRange(ContentRangeHeaderValue? range, long offset) =>
        range?.From is not null && range.From.Value == offset;

    private static bool IsAlreadyComplete(HttpResponseMessage response, long localLength)
    {
        var total = response.Content.Headers.ContentRange?.Length;
        return total.HasValue && localLength >= total.Value;
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
}
