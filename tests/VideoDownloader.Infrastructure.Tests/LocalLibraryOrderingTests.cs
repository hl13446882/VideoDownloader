using VideoDownloader.Infrastructure.LocalLibrary;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class LocalLibraryOrderingTests
{
    private sealed record Item(string? Author, DateTimeOffset At);

    [Fact]
    public void OrderByAuthorThenDownloadedAtDesc_GroupsByAuthorThenNewest()
    {
        var items = new[]
        {
            new Item("Bob", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            new Item("Alice", new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)),
            new Item("Alice", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new Item(null, new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero)),
        };

        var ordered = LocalLibraryOrdering
            .OrderByAuthorThenDownloadedAtDesc(items, i => i.Author, i => i.At)
            .ToArray();

        Assert.Equal("Alice", ordered[0].Author);
        Assert.Equal(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), ordered[0].At);
        Assert.Equal("Alice", ordered[1].Author);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ordered[1].At);
        Assert.Equal("Bob", ordered[2].Author);
        Assert.Null(ordered[3].Author);
    }
}
