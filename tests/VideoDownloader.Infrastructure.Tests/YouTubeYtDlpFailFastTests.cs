using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class YouTubeYtDlpFailFastTests
{
    [Fact]
    public void SessionRetriesUseRecentSuccessWithoutReorderingAddressMethods()
    {
        var conditions = new YouTubeSessionConditions();
        Assert.Equal(new string?[] { null, "cookie-file" }, conditions.Order("first", "cookie-file"));
        conditions.RecordSuccess("first", true);
        Assert.Equal(new string?[] { "cookie-file", null }, conditions.Order("first", "cookie-file"));
        Assert.Equal(new string?[] { null, "cookie-file" }, conditions.Order("second", "cookie-file"));
        conditions.RecordSuccess("first", false);
        Assert.Equal(new string?[] { null, "cookie-file" }, conditions.Order("first", "cookie-file"));
        Assert.Equal(new string?[] { null }, conditions.Order("first", null));
    }

    [Fact]
    public void BundledRuntime_IsResolvedIndependentlyOfProcessPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vd-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var resolver = Path.Combine(dir, "yt-dlp.exe");
            Assert.Null(YouTubeYtDlpExtractor.FindJavaScriptRuntime(resolver));
            File.WriteAllText(Path.Combine(dir, "node.exe"), "fixture");
            Assert.Equal("node:" + Path.Combine(dir, "node.exe"), YouTubeYtDlpExtractor.FindJavaScriptRuntime(resolver));
            File.WriteAllText(Path.Combine(dir, "deno.exe"), "fixture");
            Assert.Equal("deno:" + Path.Combine(dir, "deno.exe"), YouTubeYtDlpExtractor.FindJavaScriptRuntime(resolver));
        }
        finally
        {
            File.Delete(Path.Combine(dir, "node.exe"));
            File.Delete(Path.Combine(dir, "deno.exe"));
            Directory.Delete(dir);
        }
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: The page needs to be reloaded.", true)]
    [InlineData("ERROR: [youtube] abc: Requested format is not available. Use --list-formats", true)]
    [InlineData("ERROR: [youtube] abc: No video formats found!", true)]
    [InlineData("ERROR: [youtube] abc: This live event has ended.", true)]
    [InlineData("WARNING: [youtube] Skipping format", false)]
    [InlineData("ERROR: Unable to download webpage: HTTP Error 503", false)]
    public void Definitive_failure_lines_match_expected(string line, bool expected) =>
        Assert.Equal(expected, YouTubeYtDlpExtractor.IsDefinitiveYtDlpFailure(line));
}
