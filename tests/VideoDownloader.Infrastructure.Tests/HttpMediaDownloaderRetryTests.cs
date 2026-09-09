using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public class RequestContextProtectorTests
{
    [Fact]
    public void Protect_DoesNotStorePlainCookieInMetaJson()
    {
        var context = RequestContext.CreateEmpty() with
        {
            Cookies =
            [
                new BrowserCookie("session", "super-secret-test-token", "localhost", "/", null, false, true)
            ]
        };

        var (metaJson, secret) = DownloadJobMapper.SerializeVariant(
            MediaVariant.FromCombinedTrack(
                "v1",
                new Uri("http://localhost/media/cookie.mp4"),
                context,
                container: "mp4"));

        Assert.NotNull(secret);
        Assert.DoesNotContain("super-secret-test-token", metaJson);

        var restored = DownloadJobMapper.DeserializeVariant(
            "http://localhost/media/cookie.mp4",
            metaJson,
            secret);

        Assert.Contains(restored.RequestContext.Cookies, c => c.Value == "super-secret-test-token");
    }
}

public class HttpMediaDownloaderRetryTests
{
    [Fact]
    public async Task DownloadDirectAsync_RetriesOn500ThenSucceeds()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            if (attempts < 3)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ValidMp4())
            };
        });

        var client = new HttpClient(handler);
        var downloader = CreateDownloader(client, retryCount: 3);
        var job = CreateJob();

        await downloader.DownloadDirectAsync(job, null, null, CancellationToken.None);

        Assert.True(File.Exists(job.TargetPath));
        Assert.Equal(3, attempts);
        File.Delete(job.TargetPath);
    }

    [Fact]
    public async Task DownloadDirectAsync_403_ThrowsWithoutRetryInternally()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = new HttpClient(handler);
        var downloader = CreateDownloader(client, retryCount: 3);
        var job = CreateJob();

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            downloader.DownloadDirectAsync(job, null, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.Http403, ex.ErrorCode);
    }

    [Fact]
    public async Task DownloadDirectAsync_ConnectionTimeout_UsesIpv4Fallback()
    {
        var primaryAttempts = 0;
        var fallbackAttempts = 0;
        var primary = new HttpClient(new StubHandler(_ =>
        {
            primaryAttempts++;
            throw new HttpRequestException(
                "Connection timed out.",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TimedOut));
        }));
        var fallback = new HttpClient(new StubHandler(_ =>
        {
            fallbackAttempts++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ValidMp4())
            };
        }));
        var job = CreateJob();
        var options = Options.Create(new AppOptions
        {
            Download = new DownloadOptions { RetryCount = 0 }
        });
        var downloader = new HttpMediaDownloader(
            primary,
            fallback,
            new RequestMessageFactory(),
            options,
            NullLogger<HttpMediaDownloader>.Instance);

        await downloader.DownloadDirectAsync(job, null, null, CancellationToken.None);

        Assert.Equal(1, primaryAttempts);
        Assert.Equal(1, fallbackAttempts);
        Assert.True(File.Exists(job.TargetPath));
        File.Delete(job.TargetPath);
    }

    // Minimal box fixture: these transport tests validate structure, not decoding.
    private static byte[] ValidMp4()
    {
        var data = new byte[600 * 1024];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), 12);
        "moov"u8.CopyTo(data.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12, 4), data.Length - 12);
        "mdat"u8.CopyTo(data.AsSpan(16));
        return data;
    }

    private static HttpResponseMessage Partial(byte[] data, int start, int count)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(data.AsSpan(start, count).ToArray())
        };
        response.Content.Headers.ContentRange = new(start, start + count - 1, data.Length);
        return response;
    }

    [Fact]
    public async Task BrowserRangeIsReplaced_AndManyWindowsContinueWithoutQueueRetry()
    {
        var data = ValidMp4();
        var calls = 0;
        var job = CreateJob();
        job.Variant = job.Variant.WithRequestContext(RequestContext.CreateEmpty() with
        {
            Headers = new Dictionary<string, string> { ["Range"] = "bytes=819201-922541", ["If-Range"] = "old" }
        });
        using var client = new HttpClient(new StubHandler(request =>
        {
            var start = (int)request.Headers.Range!.Ranges.Single().From!.Value;
            Assert.Null(request.Headers.IfRange);
            Assert.Equal(calls++ * 16384, start);
            return Partial(data, start, Math.Min(16384, data.Length - start));
        }));
        try
        {
            await CreateDownloader(client, 0).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.True(calls > 8);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
            Assert.Equal(data.Length, job.DownloadedBytes);
        }
        finally { File.Delete(job.TargetPath); File.Delete(job.TargetPath + ".part"); }
    }

    [Fact]
    public async Task NonZeroFirstResponseIsRejectedBeforeWriting()
    {
        var job = CreateJob();
        using var client = new HttpClient(new StubHandler(_ => Partial(ValidMp4(), 8192, 100)));
        try
        {
            var ex = await Assert.ThrowsAsync<DownloadException>(() =>
                CreateDownloader(client, 0).DownloadDirectAsync(job, null, null, CancellationToken.None));
            Assert.Equal(ErrorCodes.RangeMismatch, ex.ErrorCode);
            Assert.False(File.Exists(job.TargetPath));
            Assert.Equal(0, new FileInfo(job.TargetPath + ".part").Length);
        }
        finally { File.Delete(job.TargetPath + ".part"); }
    }

    [Fact]
    public async Task FullSizeGarbageIsNotPromoted_EvenAfter416()
    {
        var job = CreateJob();
        var data = new byte[600 * 1024];
        await File.WriteAllBytesAsync(job.TargetPath + ".part", data);
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                { Content = new ByteArrayContent([]) };
            r.Content.Headers.ContentRange = new(data.Length);
            return r;
        }));
        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            CreateDownloader(client, 0).DownloadDirectAsync(job, null, null, CancellationToken.None));
        Assert.Equal(ErrorCodes.IncompleteDownload, ex.ErrorCode);
        Assert.False(File.Exists(job.TargetPath));
        Assert.False(File.Exists(job.TargetPath + ".part"));
    }

    [Fact]
    public async Task ShortBodyAndRangeMismatchCannotComplete()
    {
        var job = CreateJob();
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var r = Partial(ValidMp4(), 0, 100);
            r.Content.Headers.ContentRange = new(0, 199, 600 * 1024);
            return r;
        }));
        try
        {
            var ex = await Assert.ThrowsAsync<DownloadException>(() =>
                CreateDownloader(client, 0).DownloadDirectAsync(job, null, null, CancellationToken.None));
            Assert.Equal(ErrorCodes.IncompleteDownload, ex.ErrorCode);
            Assert.False(File.Exists(job.TargetPath));
        }
        finally { File.Delete(job.TargetPath + ".part"); }
    }

    [Fact]
    public async Task ResumeIgnoredByServerRestartsWithExactBytes()
    {
        var job = CreateJob();
        var data = ValidMp4();
        await File.WriteAllBytesAsync(job.TargetPath + ".part", new byte[4096]);
        job.TotalBytes = 999999;
        using var client = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
            { Content = new ByteArrayContent(data) }));
        try
        {
            await CreateDownloader(client, 0).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
            Assert.Equal(data.Length, job.TotalBytes);
        }
        finally { File.Delete(job.TargetPath); File.Delete(job.TargetPath + ".part"); }
    }

    [Fact]
    public async Task StaleProgressCannotChangeAuthoritativeCompletionCount()
    {
        var job = CreateJob();
        var data = ValidMp4();
        using var client = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
            { Content = new ByteArrayContent(data) }));
        try
        {
            await CreateDownloader(client, 0).DownloadDirectAsync(job,
                new CallbackProgress(_ => job.DownloadedBytes = 0), null, CancellationToken.None);
            Assert.Equal(data.Length, job.DownloadedBytes);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
        }
        finally { File.Delete(job.TargetPath); File.Delete(job.TargetPath + ".part"); }
    }

    private sealed class CallbackProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private static HttpMediaDownloader CreateDownloader(HttpClient client, int retryCount)
    {
        var options = Options.Create(new AppOptions
        {
            Download = new DownloadOptions { RetryCount = retryCount }
        });

        return new HttpMediaDownloader(
            client,
            new RequestMessageFactory(),
            options,
            NullLogger<HttpMediaDownloader>.Instance);
    }

    private static DownloadJob CreateJob()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vd-test-{Guid.NewGuid():N}.mp4");
        return new DownloadJob
        {
            Id = Guid.NewGuid(),
            DisplayName = "test",
            TargetPath = path,
            Variant = MediaVariant.FromCombinedTrack(
                "v1",
                new Uri("http://localhost/media/flaky.mp4"),
                RequestContext.CreateEmpty(),
                container: "mp4"),
            Status = DownloadStatus.Downloading,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_factory(request));
    }

}

public class SanitizedLoggerTests
{
    [Fact]
    public void SanitizeMessage_RedactsCookieHeader()
    {
        var input = "Request failed Cookie: session=super-secret-value; auth=hidden";
        var result = Logging.SanitizedLogger.SanitizeMessage(input);
        Assert.DoesNotContain("super-secret-value", result);
        Assert.DoesNotContain("auth=hidden", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void SanitizeUrl_RedactsSensitiveQuery()
    {
        var url = "https://example.com/video?token=secret123&quality=1080";
        var result = Logging.SanitizedLogger.SanitizeUrl(url);
        Assert.DoesNotContain("secret123", result);
        Assert.Contains("[REDACTED]", result);
    }
}

public class RequestMessageFactoryTests
{
    [Fact]
    public void Create_OnlySendsCookieToMatchingDomain()
    {
        var context = RequestContext.CreateEmpty() with
        {
            Cookies =
            [
                new BrowserCookie("good", "1", ".example.com", "/", null, true, true),
                new BrowserCookie("bad", "1", ".notexample.com", "/", null, true, true)
            ]
        };

        var variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://video.example.com/media.mp4"),
            context,
            container: "mp4");

        using var request = new RequestMessageFactory(Microsoft.Extensions.Options.Options.Create(
            new VideoDownloader.Infrastructure.Configuration.AppOptions { Browser = new() { CaptureCookies = true } })).Create(
            variant,
            HttpMethod.Get,
            variant.SourceUrl);

        var cookie = string.Join("; ", request.Headers.GetValues("Cookie"));
        Assert.Contains("good=1", cookie);
        Assert.DoesNotContain("bad=1", cookie);
    }

    [Fact]
    public void Create_SendsCookiesAlreadyOnContext_EvenWhenCaptureDisabled()
    {
        // BrowserObserved downloads attach jar cookies without flipping CaptureCookies.
        var context = RequestContext.CreateEmpty() with
        {
            Cookies = [new BrowserCookie("tt", "1", ".tiktok.com", "/", null, true, true)]
        };
        var variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://v16-webapp-prime.tiktok.com/video/x.mp4"),
            context,
            container: "mp4");

        using var request = new RequestMessageFactory().Create(variant, HttpMethod.Get, variant.SourceUrl);
        Assert.Contains("tt=1", string.Join("; ", request.Headers.GetValues("Cookie")));
    }

    [Fact]
    public void Create_CookieDisabled_RejectsExplicitCookieHeaderFromResolver()
    {
        var context = RequestContext.CreateEmpty() with
        {
            Headers = new Dictionary<string, string>
            {
                ["Cookie"] = "cdn-token=resolver"
            }
        };

        var variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://v16.tiktokcdn.com/media.mp4"),
            context,
            container: "mp4");

        using var request = new RequestMessageFactory().Create(
            variant,
            HttpMethod.Get,
            variant.SourceUrl);

        Assert.False(request.Headers.Contains("Cookie"));
    }
}

public class PathValidatorTests
{
    [Fact]
    public void IsValidSaveDirectory_AcceptsUserDownloads()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        Assert.True(Security.PathValidator.IsValidSaveDirectory(path));
    }

    [Fact]
    public void IsValidSaveDirectory_RejectsRelativePath()
    {
        Assert.False(Security.PathValidator.IsValidSaveDirectory("relative/path"));
    }
}
