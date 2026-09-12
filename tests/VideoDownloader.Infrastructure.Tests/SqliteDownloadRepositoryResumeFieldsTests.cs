using Microsoft.Extensions.Options;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public class SqliteDownloadRepositoryResumeFieldsTests
{
    [Fact]
    public async Task Save_RoundTrips_Caption_Duration_ExpectedTotal_And_RewritesVariant()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "vd-resume-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = Options.Create(new AppOptions
            {
                Database = new DatabaseOptions { Path = dbPath }
            });
            var repo = new SqliteDownloadRepository(options);
            await repo.InitializeAsync();

            var page = new Uri("https://www.example.com/watch/1");
            var original = MediaVariant.FromCombinedTrack(
                "v1",
                new Uri("https://cdn-a.example/a.mp4"),
                RequestContext.CreateEmpty(),
                height: 720,
                container: "mp4",
                contentLength: 12_345_678) with
            {
                ContentIdentity = "id:1",
                RecoveryPageUrl = page
            };

            var job = new DownloadJob
            {
                Id = Guid.NewGuid(),
                DisplayName = "短标题_1分_720P_12MB",
                Caption = "这是完整文案，远长于文件名限制",
                DurationSec = 95.5,
                Variant = original,
                PageUrl = page,
                TargetPath = Path.Combine(Path.GetTempPath(), "a.mp4"),
                Status = DownloadStatus.Paused,
                DownloadedBytes = 1_000_000,
                TotalBytes = 12_345_678,
                ExpectedTotalBytes = 12_345_678,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await repo.SaveAsync(job);

            var renewed = original with
            {
                Tracks =
                [
                    original.Tracks[0] with
                    {
                        SourceUrl = new Uri("https://cdn-b.example/b.mp4")
                    }
                ]
            };
            job.Variant = renewed;
            job.DownloadedBytes = 2_000_000;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await repo.SaveAsync(job);

            var loaded = await repo.GetByIdAsync(job.Id);
            Assert.NotNull(loaded);
            Assert.Equal("这是完整文案，远长于文件名限制", loaded!.Caption);
            Assert.Equal(95.5, loaded.DurationSec);
            Assert.Equal(12_345_678, loaded.ExpectedTotalBytes);
            Assert.Equal(2_000_000, loaded.DownloadedBytes);
            Assert.Equal("https://cdn-b.example/b.mp4", loaded.Variant.SourceUrl.AbsoluteUri);
            Assert.Equal("id:1", loaded.Variant.ContentIdentity);
            Assert.Equal(720, loaded.Variant.Height);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}
