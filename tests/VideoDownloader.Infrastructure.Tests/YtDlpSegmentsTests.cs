using System.Text.Json;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Tests;

public class YtDlpSegmentsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ReadUrls_KeepsFragmentOrder()
    {
        using var document = JsonDocument.Parse("""
            {"protocol":"http_dash_segments","fragments":[
              {"url":"https://cdn.test/frag/0"},
              {"url":"https://cdn.test/frag/1"},
              {"url":"https://cdn.test/frag/2"}]}
            """);

        var urls = YtDlpSegments.ReadUrls(document.RootElement);

        Assert.NotNull(urls);
        Assert.Equal(3, urls!.Count);
        Assert.Equal("https://cdn.test/frag/0", urls[0].AbsoluteUri);
        Assert.Equal("https://cdn.test/frag/2", urls[2].AbsoluteUri);
    }

    [Fact]
    public void ReadUrls_RequiresMoreThanOneUsableFragment()
    {
        using var document = JsonDocument.Parse("""
            {"protocol":"http_dash_segments","fragments":[{"url":"https://cdn.test/frag/0"}]}
            """);

        Assert.Null(YtDlpSegments.ReadUrls(document.RootElement));
    }

    [Theory]
    [InlineData("""{"protocol":"http_dash_segments","url":"https://cdn.test/template"}""", false)]
    [InlineData("""{"protocol":"http_dash_segments","url":"https://cdn.test/template","fragments":[{"url":"https://cdn.test/0"},{"url":"https://cdn.test/1"}]}""", true)]
    [InlineData("""{"protocol":"https","url":"https://cdn.test/video.mp4"}""", true)]
    public void HasUsableAddress_Rejects_Template_Without_Fragments(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        var url = document.RootElement.GetProperty("url").GetString();

        Assert.Equal(expected, YtDlpSegments.HasUsableAddress(document.RootElement, url));
    }

    [Fact]
    public void Attach_Points_Source_At_First_Fragment()
    {
        using var document = JsonDocument.Parse("""
            {"protocol":"http_dash_segments","fragments":[
              {"url":"https://cdn.test/frag/0"},
              {"url":"https://cdn.test/frag/1"}]}
            """);
        var track = new MediaTrack("v", MediaTrackKind.Video,
            new Uri("https://cdn.test/template"), "avc1", "mp4", 1000, null,
            RequestContext.CreateEmpty());

        var attached = YtDlpSegments.Attach(document.RootElement, track);

        Assert.Equal("https://cdn.test/frag/0", attached.SourceUrl.AbsoluteUri);
        Assert.Equal(2, attached.HttpDashFragmentCount);
        Assert.Equal(["https://cdn.test/frag/0", "https://cdn.test/frag/1"],
            attached.HttpDashFragmentUrls!.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void Attach_Leaves_Plain_Format_Unchanged()
    {
        using var document = JsonDocument.Parse("""{"protocol":"https","url":"https://cdn.test/video.mp4"}""");
        var track = new MediaTrack("v", MediaTrackKind.Video,
            new Uri("https://cdn.test/video.mp4"), "avc1", "mp4", 1000, null,
            RequestContext.CreateEmpty());

        Assert.Same(track, YtDlpSegments.Attach(document.RootElement, track));
    }
}
