using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Subtitles;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class LocalPlaybackSubtitleTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "VideoDownloader.SubtitleTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Resolver_maps_local_player_job_to_physical_file_and_versioned_identity()
    {
        Directory.CreateDirectory(_tempRoot);
        var path = Path.Combine(_tempRoot, "sample.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

        var jobId = Guid.NewGuid();
        var job = new DownloadJob
        {
            Id = jobId,
            DisplayName = "sample",
            TargetPath = path,
            CreatedAt = DateTimeOffset.UtcNow
        };
        SetCompleted(job);

        var repository = Substitute.For<IDownloadRepository>();
        repository.GetByIdAsync(jobId, Arg.Any<CancellationToken>()).Returns(job);
        var resolver = new LocalPlaybackMediaSourceResolver(repository);

        var first = await resolver.ResolveAsync($"http://127.0.0.1:17890/play/{jobId:N}?group=time");

        Assert.NotNull(first);
        Assert.Equal(jobId, first!.JobId);
        Assert.Equal(Path.GetFullPath(path), first.FilePath);
        Assert.StartsWith($"local:{jobId:N}:4:", first.CacheIdentity, StringComparison.Ordinal);

        await File.AppendAllTextAsync(path, "changed");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var second = await resolver.ResolveAsync($"http://127.0.0.1:17890/play/{jobId:N}");

        Assert.NotNull(second);
        Assert.NotEqual(first.CacheIdentity, second!.CacheIdentity);
    }

    [Fact]
    public async Task Resolver_ignores_non_local_player_pages()
    {
        var repository = Substitute.For<IDownloadRepository>();
        var resolver = new LocalPlaybackMediaSourceResolver(repository);

        var result = await resolver.ResolveAsync("https://example.com/play/0123456789abcdef0123456789abcdef");

        Assert.Null(result);
        await repository.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    private static void SetCompleted(DownloadJob job)
    {
        typeof(DownloadJob)
            .GetProperty(nameof(DownloadJob.Status))!
            .SetValue(job, DownloadStatus.Completed);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best effort test cleanup.
        }
    }
}
