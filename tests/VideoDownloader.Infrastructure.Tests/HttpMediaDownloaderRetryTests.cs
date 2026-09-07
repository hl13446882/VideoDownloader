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
                Content = new ByteArrayContent(new byte[1024])
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
                Content = new ByteArrayContent(new byte[1024])
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

        using var request = new RequestMessageFactory().Create(
            variant,
            HttpMethod.Get,
            variant.SourceUrl);

        var cookie = string.Join("; ", request.Headers.GetValues("Cookie"));
        Assert.Contains("good=1", cookie);
        Assert.DoesNotContain("bad=1", cookie);
    }

    [Fact]
    public void Create_PreservesExplicitCookieHeaderFromResolver()
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

        var cookie = string.Join("; ", request.Headers.GetValues("Cookie"));
        Assert.Contains("cdn-token=resolver", cookie);
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
