using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Sites.ExternalResolvers;

namespace VideoDownloader.Infrastructure.Tests;

public class YtDlpResolverFormatTests
{
    [Fact]
    public void ParseJson_RetainsAdaptiveQualitiesAndIndependentAudio()
    {
        const string json = """
            {
              "id":"sample", "title":"Adaptive sample",
              "formats":[
                {"format_id":"18","url":"https://media.example/360.mp4","ext":"mp4","height":360,"tbr":700,"filesize":7000000,"vcodec":"avc1","acodec":"mp4a","protocol":"https"},
                {"format_id":"137","url":"https://media.example/1080.mp4","ext":"mp4","height":1080,"tbr":4000,"filesize":40000000,"vcodec":"avc1","acodec":"none","protocol":"https"},
                {"format_id":"136","url":"https://media.example/720.mp4","ext":"mp4","height":720,"tbr":2000,"filesize":20000000,"vcodec":"avc1","acodec":"none","protocol":"https"},
                {"format_id":"140","url":"https://media.example/audio.m4a","ext":"m4a","tbr":128,"filesize":2000000,"vcodec":"none","acodec":"mp4a","protocol":"https"},
                {"format_id":"251","url":"https://media.example/audio.webm","ext":"webm","tbr":160,"filesize":2500000,"vcodec":"none","acodec":"opus","protocol":"https"}
              ]
            }
            """;
        var resolver = new YtDlpResolver(Options.Create(new AppOptions()), NullLogger<YtDlpResolver>.Instance);

        var video = Assert.Single(resolver.ParseJson(json, new Uri("https://page.example/watch"),
            RequestContext.CreateEmpty(), SiteIds.Generic));

        Assert.Contains(video.Variants, variant => variant.Height == 1080 && variant.Tracks.Count == 2);
        Assert.Contains(video.Variants, variant => variant.Height == 720 && variant.Tracks.Count == 2);
        Assert.Contains(video.Variants, variant => variant.Height == 360 && variant.Tracks.Any(t => t.Kind == MediaTrackKind.Combined));
        Assert.Equal(2, video.Variants.Count(variant => variant.Tracks.All(t => t.Kind == MediaTrackKind.Audio)));
        Assert.Equal(1080, video.Variants.First().Height);
    }
}
