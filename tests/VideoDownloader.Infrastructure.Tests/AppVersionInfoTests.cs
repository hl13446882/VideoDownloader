using VideoDownloader.Infrastructure.Update;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class AppVersionInfoTests
{
    [Theory]
    [InlineData("0.2.1-beta", "0.2.0-beta", 1)]
    [InlineData("0.2.0-beta", "0.2.1-beta", -1)]
    [InlineData("0.2.0-beta", "0.2.0-beta", 0)]
    [InlineData("1.0.0", "1.0.0-beta", 1)]
    [InlineData("0.2.1-beta+abc", "0.2.1-beta", 0)]
    public void CompareSemVer_Works(string left, string right, int expectedSign)
    {
        var cmp = AppVersionInfo.CompareSemVer(left, right);
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(cmp));
    }

    [Fact]
    public void Normalize_StripsBuildMetadata()
    {
        Assert.Equal("0.2.1-beta", AppVersionInfo.Normalize("0.2.1-beta+deadbeef"));
    }
}
