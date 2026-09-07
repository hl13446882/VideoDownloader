using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
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
    }

    [Fact]
    public void FilesAndPermissions_AreNotReportedAsTimeouts()
    {
        Assert.Equal(ErrorCodes.FileIo,DownloadEngine.ClassifyError(new IOException()));
        Assert.Equal(ErrorCodes.PermissionDenied,DownloadEngine.ClassifyError(new UnauthorizedAccessException()));
        Assert.Equal(ErrorCodes.InvalidFormat,DownloadEngine.ClassifyError(new InvalidDataException()));
        Assert.Equal(ErrorCodes.NetTimeout,DownloadEngine.ClassifyError(new HttpRequestException()));
    }
}
