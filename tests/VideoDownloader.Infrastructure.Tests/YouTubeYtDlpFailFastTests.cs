using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class YouTubeYtDlpFailFastTests
{
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
