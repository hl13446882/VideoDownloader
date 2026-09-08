using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Http;

namespace VideoDownloader.Infrastructure.Tests;

public class ProbeSampleGateBrowserObservedTests
{
    [Fact]
    public async Task ValidateAndRecover_PrefersBrowserObservedVideo_SkipsForbiddenYtDlpSample()
    {
        var page = new Uri("https://www.tiktok.com/@i/video/123");
        var browserUrl = new Uri("https://v16-webapp-prime.tiktok.com/video/tos/browser-object/x");
        var ytdlpUrl = new Uri("https://v16-webapp-prime.tiktok.com/video/tos/other-object/y");
        var ctx = RequestContext.CreateEmpty();

        var browserTrack = new MediaTrack("b", MediaTrackKind.Combined, browserUrl, null, "mp4", null, null, ctx)
        {
            IsValidated = true,
            BrowserObserved = true
        };
        var ytdlpTrack = new MediaTrack("y", MediaTrackKind.Combined, ytdlpUrl, null, "mp4", null, null, ctx);

        var video = new DetectedVideo(
            Guid.NewGuid(),
            "tiktok",
            "123",
            "title",
            page,
            MediaFamily.DirectMp4,
            [
                MediaVariant.FromTracks("browser", null, null, null, "mp4", [browserTrack]),
                MediaVariant.FromTracks("ytdlp", null, null, null, "mp4", [ytdlpTrack])
            ],
            false);

        var clients = Substitute.For<IHttpClientFactory>();
        var handler = new Always403Handler();
        clients.CreateClient("media-primary").Returns(_ => new HttpClient(handler, false));
        var validator = new MediaAvailabilityValidator(
            clients,
            new RequestMessageFactory(),
            NullLogger<MediaAvailabilityValidator>.Instance);
        var resolver = Substitute.For<IExternalSiteResolver>();
        var decisions = new List<ProbeCandidateDecision>();

        var (result, failure) = await ProbeSampleGate.ValidateAndRecoverAsync(
            video,
            page,
            "id:123",
            ctx,
            resolver,
            validator,
            () => true,
            NullLogger.Instance,
            decisions.Add,
            CancellationToken.None);

        Assert.Null(failure);
        Assert.NotNull(result);
        Assert.Contains(result!.Variants, v => v.Tracks.Any(t => t.BrowserObserved));
        Assert.DoesNotContain(result.Variants, v => v.SourceUrl == ytdlpUrl);
        Assert.Contains(decisions, d => d.Reason == "browser_observed");
        Assert.Equal(0, handler.Calls); // must not sample when browser video accepted
    }

    private sealed class Always403Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }
}
