using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private static byte[] ValidMp4() => ValidSizedMp4(600 * 1024);

    private static byte[] ValidSizedMp4(int length)
    {
        var data = new byte[length];
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
    public async Task Aligned206Resume_KeepsProgress_WhenEtagDrifts()
    {
        var data = ValidMp4();
        var prefix = data.AsSpan(0, 8192).ToArray();
        var job = CreateJob();
        await File.WriteAllBytesAsync(job.TargetPath + ".part", prefix);
        job.DownloadedBytes = prefix.Length;
        job.TotalBytes = data.Length;
        job.ETag = "\"old-etag\"";
        job.LastModified = "Mon, 01 Jan 2024 00:00:00 GMT";

        using var client = new HttpClient(new StubHandler(request =>
        {
            var start = (int)request.Headers.Range!.Ranges.Single().From!.Value;
            Assert.Equal(prefix.Length, start);
            var response = Partial(data, start, data.Length - start);
            response.Headers.ETag = new EntityTagHeaderValue("\"new-etag\"");
            response.Content.Headers.LastModified = DateTimeOffset.Parse("Tue, 02 Jan 2024 00:00:00 GMT");
            return response;
        }));

        try
        {
            await CreateDownloader(client, 0).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
            Assert.Equal("\"new-etag\"", job.ETag);
            Assert.False(File.Exists(job.TargetPath + ".part"));
        }
        finally
        {
            if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
            if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
        }
    }

    [Fact]
    public async Task Aligned206Resume_Resets_WhenContentLengthChanges()
    {
        var data = ValidMp4();
        var prefix = data.AsSpan(0, 8192).ToArray();
        var job = CreateJob();
        await File.WriteAllBytesAsync(job.TargetPath + ".part", prefix);
        job.DownloadedBytes = prefix.Length;
        job.TotalBytes = data.Length + 1000; // recorded length no longer matches CDN
        job.ETag = "\"same\"";

        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                // First resume attempt reports a different total → RangeMismatch → reset → retry from 0.
                var response = Partial(data, prefix.Length, 100);
                return response;
            }

            return Partial(data, 0, data.Length);
        }));

        try
        {
            await CreateDownloader(client, retryCount: 1).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.Equal(2, calls);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
        }
        finally
        {
            if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
            if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
        }
    }

    [Fact]
    public async Task BilibiliAligned206Resume_KeepsProgress_WhenTotalJittersByKilobytes()
    {
        var data = ValidMp4();
        var prefix = data.AsSpan(0, 8192).ToArray();
        var job = CreateJob();
        job.Variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://upos-sz-mirrorbos.bilivideo.com/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=bos"),
            RequestContext.CreateEmpty(),
            container: "mp4");
        await File.WriteAllBytesAsync(job.TargetPath + ".part", prefix);
        job.DownloadedBytes = prefix.Length;
        job.TotalBytes = data.Length + 4725;

        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            var start = prefix.Length;
            return Partial(data, start, data.Length - start);
        }));

        try
        {
            await CreateDownloader(client, retryCount: 0).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.Equal(1, calls);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
            Assert.Equal(data.Length, job.TotalBytes);
        }
        finally
        {
            if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
            if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
        }
    }

    [Fact]
    public async Task BilibiliConnectionReset_ResumesFromPart_WithoutRestart()
    {
        var data = ValidMp4();
        var resetAfter = 8192;
        var job = CreateJob();
        job.Variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/a/b/41747222317/41747222317-1-30080.m4s?os=cosovbv"),
            RequestContext.CreateEmpty(),
            container: "mp4");

        var calls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            calls++;
            var start = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            if (calls == 1)
            {
                Assert.Equal(0, start);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ConnectionResetStream(data, resetAfter))
                };
                response.Content.Headers.ContentLength = data.Length;
                return response;
            }

            Assert.Equal(resetAfter, start);
            return Partial(data, start, data.Length - start);
        }));

        try
        {
            await CreateDownloader(client, retryCount: 0).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.True(calls >= 2);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
            Assert.Equal(data.Length, job.DownloadedBytes);
        }
        finally
        {
            if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
            if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
        }
    }

    [Fact]
    public async Task TikTokSignedCdn_FirstGet_OmitsRangeHeader()
    {
        var data = ValidMp4();
        var job = CreateJob();
        job.Variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://v16-webapp-prime.tiktok.com/video/tos/alisg/x.mp4"),
            RequestContext.CreateEmpty(),
            container: "mp4");

        RangeHeaderValue? seenRange = new(0, 1);
        using var client = new HttpClient(new StubHandler(request =>
        {
            seenRange = request.Headers.Range;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            };
        }));

        try
        {
            await CreateDownloader(client, retryCount: 0).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.Null(seenRange);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
        }
        finally
        {
            if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
            if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
        }
    }

    [Fact]
    public async Task TikTokSignedCdn_AcceptsTruncatedBody_WhenAtLeast256KiB()
    {
        var prefix = ValidSizedMp4(300 * 1024);
        var job = CreateJob();
        job.Variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://v16-webapp-prime.tiktok.com/video/tos/alisg/x.mp4"),
            RequestContext.CreateEmpty(),
            container: "mp4");

        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(prefix)
            };
            response.Content.Headers.ContentLength = prefix.Length * 2;
            return response;
        }));

        try
        {
            await CreateDownloader(client, retryCount: 0).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.Equal(prefix, await File.ReadAllBytesAsync(job.TargetPath));
            Assert.Equal(prefix.Length, job.DownloadedBytes);
        }
        finally
        {
            if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
            if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
        }
    }

    [Fact]
    public async Task DouyinHostTruncatedBody_StillFails()
    {
        var data = ValidMp4();
        var prefix = data.AsSpan(0, 300 * 1024).ToArray();
        var job = CreateJob();
        job.Variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri("https://v3-dy-o.zjcdn.com/video/tos/cn/x.mp4"),
            RequestContext.CreateEmpty(),
            container: "mp4");

        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(prefix)
            };
            response.Content.Headers.ContentLength = data.Length;
            return response;
        }));

        try
        {
            var ex = await Assert.ThrowsAsync<DownloadException>(() =>
                CreateDownloader(client, retryCount: 0).DownloadDirectAsync(job, null, null, CancellationToken.None));
            Assert.Equal(ErrorCodes.IncompleteDownload, ex.ErrorCode);
        }
        finally
        {
            if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
            if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
        }
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

    private static HttpMediaDownloader CreateDownloader(
        HttpClient client,
        int retryCount,
        int parallelConnections = 1,
        long parallelMinBytes = 4L * 1024 * 1024)
    {
        var options = Options.Create(new AppOptions
        {
            Download = new DownloadOptions
            {
                RetryCount = retryCount,
                // Keep unit tests on the single-connection path unless a case opts in.
                ParallelConnections = parallelConnections,
                ParallelMinBytes = parallelMinBytes
            }
        });

        return new HttpMediaDownloader(
            client,
            new RequestMessageFactory(),
            options,
            NullLogger<HttpMediaDownloader>.Instance);
    }

    [Fact]
    public void SplitByteRanges_CoversFullObjectWithoutGapsOrOverlap()
    {
        var ranges = HttpMediaDownloader.SplitByteRanges(8_000_000, 4);
        Assert.Equal(4, ranges.Length);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(7_999_999, ranges[^1].End);
        for (var i = 1; i < ranges.Length; i++)
            Assert.Equal(ranges[i - 1].End + 1, ranges[i].Start);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadDirectAsync_ParallelRanges_AssemblesGoogleVideoObject(bool redirect)
    {
        var data = ValidSizedMp4(5 * 1024 * 1024);
        var rangeHits = 0;
        var redirectHits = 0;
        var handler = new StubHandler(request =>
        {
            Assert.NotNull(request.Headers.Range!.Ranges.Single().To);
            Assert.Equal("identity", request.Headers.AcceptEncoding.ToString());
            if (redirect && request.RequestUri!.Host.StartsWith("rr1"))
            {
                Interlocked.Increment(ref redirectHits);
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://rr2.googlevideo.com/videoplayback?id=1");
                return response;
            }
            Interlocked.Increment(ref rangeHits);
            Assert.NotNull(request.Headers.Range);
            var range = request.Headers.Range!.Ranges.Single();
            var from = (int)range.From!.Value;
            var to = (int)(range.To ?? data.Length - 1);
            Assert.InRange(to - from + 1, 1, 1024 * 1024);
            return Partial(data, from, to - from + 1);
        });

        var client = new HttpClient(handler);
        var downloader = CreateDownloader(client, retryCount: 0, parallelConnections: 4, parallelMinBytes: 1024 * 1024);
        var path = Path.Combine(Path.GetTempPath(), $"vd-par-{Guid.NewGuid():N}.mp4");
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            DisplayName = "yt-parallel",
            TargetPath = path,
            TotalBytes = data.Length,
            Variant = MediaVariant.FromCombinedTrack(
                "v1",
                new Uri("https://rr1---sn-test.googlevideo.com/videoplayback?id=1"),
                RequestContext.CreateEmpty(),
                container: "mp4",
                contentLength: data.Length),
            Status = DownloadStatus.Downloading,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await downloader.DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.True(File.Exists(job.TargetPath));
            Assert.Equal(data.Length, new FileInfo(job.TargetPath).Length);
            Assert.Equal(8, rangeHits);
            Assert.Equal(redirect ? 8 : 0, redirectHits);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
            Assert.False(Directory.Exists(job.TargetPath + ".part.chunks"));
        }
        finally
        {
            if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
            if (File.Exists(job.TargetPath + ".part")) File.Delete(job.TargetPath + ".part");
            var chunks = job.TargetPath + ".part.chunks";
            if (Directory.Exists(chunks)) Directory.Delete(chunks, true);
        }
    }

    [Fact]
    public async Task ParallelRanges_SlowReads_PublishBeforeTwoMiB()
    {
        var data = ValidSizedMp4(2 * 1024 * 1024);
        using var client = new HttpClient(new StubHandler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = (int)range.From!.Value;
            var end = (int)range.To!.Value;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new SlowReadStream(data[start..(end + 1)]))
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
            return response;
        }));
        var job = CreateGoogleJob(data.Length);
        var reports = new List<long>();
        try
        {
            await CreateDownloader(client, 0, 2, 1024).DownloadDirectAsync(job,
                new CallbackProgress(bytes => reports.Add(bytes)), null, CancellationToken.None);
            Assert.Contains(reports, bytes => bytes > 0 && bytes < 1024 * 1024);
            Assert.True(reports.Zip(reports.Skip(1)).All(p => p.First <= p.Second));
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
        }
        finally { CleanupGoogleJob(job); }
    }

    [Fact]
    public async Task ParallelRanges_ConnectionReset_RetriesOnlyMissingBytes()
    {
        var data = ValidSizedMp4(2 * 1024 * 1024);
        var failed = 0;
        var resumed = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = (int)range.From!.Value;
            var end = (int)range.To!.Value;
            if (start == 8192) Interlocked.Increment(ref resumed);
            if (start == 0 && Interlocked.Exchange(ref failed, 1) == 0)
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StreamContent(new ConnectionResetStream(data[..(end + 1)], 8192))
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
                return response;
            }
            return Partial(data, start, end - start + 1);
        }));
        var job = CreateGoogleJob(data.Length);
        try
        {
            await CreateDownloader(client, 1, 2, 1024).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.Equal(1, resumed);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
        }
        finally { CleanupGoogleJob(job); }
    }

    [Fact]
    public async Task GoogleVideo_LegacyPart_ResumesWithSmallRequests()
    {
        var data = ValidSizedMp4(3 * 1024 * 1024);
        var starts = new List<long>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = (int)range.From!.Value;
            var end = (int)range.To!.Value;
            starts.Add(start);
            Assert.InRange(end - start + 1, 1, 1024 * 1024);
            return Partial(data, start, end - start + 1);
        }));
        var job = CreateGoogleJob(data.Length);
        try
        {
            await File.WriteAllBytesAsync(job.TargetPath + ".part", data[..8192]);
            await CreateDownloader(client, 0, 2, 1024).DownloadDirectAsync(job, null, null, CancellationToken.None);
            Assert.Equal(8192, starts[0]);
            Assert.Equal(3, starts.Count);
            Assert.Equal(data, await File.ReadAllBytesAsync(job.TargetPath));
        }
        finally { CleanupGoogleJob(job); }
    }

    private static DownloadJob CreateGoogleJob(int size)
    {
        var job = CreateJob();
        job.TotalBytes = size;
        job.Variant = MediaVariant.FromCombinedTrack("test",
            new Uri("https://rr1.googlevideo.com/videoplayback"), RequestContext.CreateEmpty(),
            container: "mp4", contentLength: size);
        return job;
    }

    private static void CleanupGoogleJob(DownloadJob job)
    {
        File.Delete(job.TargetPath);
        File.Delete(job.TargetPath + ".part");
        if (Directory.Exists(job.TargetPath + ".part.chunks"))
            Directory.Delete(job.TargetPath + ".part.chunks", true);
    }

    private sealed class SlowReadStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(100, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 32768)], cancellationToken);
        }
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

    private sealed class ConnectionResetStream : MemoryStream
    {
        private readonly int _resetAfter;
        private int _consumed;

        public ConnectionResetStream(byte[] data, int resetAfter) : base(data, writable: false)
        {
            _resetAfter = resetAfter;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_consumed >= _resetAfter)
            {
                throw new IOException(
                    "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.",
                    new System.Net.Sockets.SocketException(10054));
            }

            var capped = Math.Min(count, _resetAfter - _consumed);
            var n = base.Read(buffer, offset, capped);
            _consumed += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_consumed >= _resetAfter)
            {
                throw new IOException(
                    "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.",
                    new System.Net.Sockets.SocketException(10054));
            }

            var capped = Math.Min(buffer.Length, _resetAfter - _consumed);
            var n = await base.ReadAsync(buffer[..capped], cancellationToken);
            _consumed += n;
            return n;
        }
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
