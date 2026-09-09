using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Download;

namespace VideoDownloader.Infrastructure.Tests;

public class DownloadPolicyAcceptanceTests
{
    [Fact]
    public async Task ExpiredAddress_IsResolvedAgain_NotOnlyReheadered()
    {
        var page = new Uri("https://example.test/watch?id=100");
        var context = RequestContext.CreateEmpty();
        var old = MediaVariant.FromCombinedTrack("720",new Uri("https://cdn.test/movie.mp4?token=old"),context,height:720);
        var fresh = old with {Tracks=[old.Tracks[0] with {SourceUrl=new Uri("https://cdn.test/movie.mp4?token=new")}]};
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        resolver.ResolveAsync(page,context,Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>(
            [new(Guid.NewGuid(),"generic","100","caption",page,MediaFamily.DirectMp4,[fresh],false)]));
        var renewed = await MediaAddressRenewal.ResolveAsync(page,old,context,[resolver],default);
        Assert.Equal(fresh.SourceUrl,renewed.SourceUrl);
        Assert.NotEqual(old.SourceUrl,renewed.SourceUrl);
    }

    [Fact]
    public async Task MutableFeed_RenewsViaContentIdentityPermalink()
    {
        var page = new Uri("https://www.douyin.com/?recommend=1");
        var detail = new Uri("https://www.douyin.com/video/100");
        var context = RequestContext.CreateEmpty();
        var old = MediaVariant.FromCombinedTrack("720", new Uri("https://cdn.test/movie.mp4?token=old"), context, height: 720) with
        {
            ContentIdentity = "id:100",
            RecoveryPageUrl = page
        };
        var fresh = old with
        {
            Tracks = [old.Tracks[0] with { SourceUrl = new Uri("https://cdn.test/movie.mp4?token=new") }],
            RecoveryPageUrl = detail
        };
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        resolver.ResolveAsync(detail, context, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>(
            [new(Guid.NewGuid(), "douyin", "100", "caption", detail, MediaFamily.DirectMp4, [fresh], false)]));
        var renewed = await MediaAddressRenewal.ResolveAsync(page, old, context, [resolver], default);
        Assert.Equal(fresh.SourceUrl, renewed.SourceUrl);
        await resolver.Received(1).ResolveAsync(detail, context, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MutableFeed_CannotRenewToAnotherVideo()
    {
        var variant = MediaVariant.FromCombinedTrack("v",new Uri("https://cdn.test/a.mp4"),RequestContext.CreateEmpty());
        var ex = await Assert.ThrowsAsync<DownloadException>(()=>MediaAddressRenewal.ResolveAsync(new Uri("https://example.test/feed"),
            variant,variant.RequestContext,[],default));
        Assert.Equal(ErrorCodes.ContextExpired,ex.ErrorCode);
    }

    [Fact]
    public void AudioExtractionAndManifestTracks_UseMediaBackend()
    {
        var router = new DownloadBackendRouter();
        var variant = MediaVariant.FromCombinedTrack("v",new Uri("https://cdn.test/a.mp4"),RequestContext.CreateEmpty());
        Assert.Equal(DownloadBackendKind.FfmpegRemux,router.Resolve(variant with {Tracks=[variant.Tracks[0] with {TrackId="audio-extract",Kind=MediaTrackKind.Audio}]}));
        Assert.Equal(DownloadBackendKind.FfmpegRemux,router.Resolve(variant with {Tracks=[variant.Tracks[0] with {Container="hls"},variant.Tracks[0]]}));
        Assert.Null((variant with {Tracks=[variant.Tracks[0] with {ContentLength=10},variant.Tracks[0]]}).TotalContentLength);

        var ctx = RequestContext.CreateEmpty();
        var album = MediaVariant.FromTracks(
            "album",
            null,
            null,
            null,
            "album",
            [
                new MediaTrack("i0", MediaTrackKind.Image, new Uri("https://cdn.test/a.jpg"), null, "image", null, null, ctx),
                new MediaTrack("a", MediaTrackKind.Audio, new Uri("https://cdn.test/a.m4a"), null, "m4a", null, null, ctx)
            ]);
        Assert.Equal(DownloadBackendKind.FfmpegAlbumSlideshow, router.Resolve(album));
    }

    [Fact]
    public void FilesAndPermissions_AreNotReportedAsTimeouts()
    {
        Assert.Equal(ErrorCodes.FileIo,DownloadEngine.ClassifyError(new IOException()));
        Assert.Equal(ErrorCodes.PermissionDenied,DownloadEngine.ClassifyError(new UnauthorizedAccessException()));
        Assert.Equal(ErrorCodes.InvalidFormat,DownloadEngine.ClassifyError(new InvalidDataException()));
        Assert.Equal(ErrorCodes.NetTimeout,DownloadEngine.ClassifyError(new HttpRequestException()));
    }

    [Fact]
    public async Task RejectedAddress_SwitchesToDurableAlternateHost()
    {
        var context = RequestContext.CreateEmpty();
        var recovery = new Uri("https://www.douyin.com/video/100");
        var rejected = MediaVariant.FromCombinedTrack(
            "web-prime",
            new Uri("https://v3-web-prime.douyinvod.com/video/tos/cn/obj/a.mp4"),
            context) with
        {
            ContentIdentity = "id:100",
            RecoveryPageUrl = recovery,
            Alternatives =
            [
                MediaVariant.FromCombinedTrack(
                    "zjcdn",
                    new Uri("https://v3-dy-o.zjcdn.com/video/tos/cn/obj/b.mp4"),
                    context) with
                {
                    ContentIdentity = "id:100",
                    RecoveryPageUrl = recovery
                }
            ]
        };

        var switched = await MediaAddressRenewal.TryAlternativesAsync(rejected, validate: null, CancellationToken.None);
        Assert.NotNull(switched);
        Assert.Contains("zjcdn", switched!.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MediaDescriptorMapper_WiresSiblingFormatsAsAlternatives()
    {
        var ctx = RequestContext.CreateEmpty();
        var page = new Uri("https://www.douyin.com/video/100");
        var webPrime = MediaVariant.FromCombinedTrack(
            "a", new Uri("https://v3-web-prime.douyinvod.com/video/tos/cn/obj/a.mp4"), ctx);
        var zjcdn = MediaVariant.FromCombinedTrack(
            "b", new Uri("https://v3-dy-o.zjcdn.com/video/tos/cn/obj/b.mp4"), ctx);
        var descriptor = new MediaDescriptor(
            "douyin",
            page,
            "100",
            MediaContentType.Video,
            zjcdn.Tracks[0],
            null,
            [],
            ctx,
            0.9)
        {
            Formats = [webPrime, zjcdn]
        };

        var video = MediaDescriptorMapper.ToDetectedVideo(descriptor, Guid.NewGuid());
        Assert.Contains(video.Variants, v => v.SourceUrl.Host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase));
        var primary = video.Variants.First(v => v.Tracks.Any(t => t.Kind == MediaTrackKind.Combined));
        Assert.Contains("zjcdn", primary.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(primary.Alternatives, a => a.SourceUrl.Host.Contains("web-prime", StringComparison.OrdinalIgnoreCase));
    }
}
