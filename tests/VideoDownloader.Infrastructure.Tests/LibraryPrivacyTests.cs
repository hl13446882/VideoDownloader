using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Ffmpeg;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.LocalLibrary;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class LibraryPrivacyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnlockRepairsRenameAndPlaysUsingRealCookies(bool legacyFormat)
    {
        using var f = await Fixture.Create();
        var encrypted = Path.Combine(f.Root, "0123456789abcdef.mp4");
        byte[] legacy;
        if (legacyFormat)
        {
            var material = VideoEncryptionService.DeriveLegacyKeyMaterial("correct");
            using var aes = Aes.Create();
            aes.Key = material[..32];
            aes.IV = material[32..];
            using var encryptor = aes.CreateEncryptor();
            legacy = "VDENC"u8.ToArray().Concat(encryptor.TransformFinalBlock(f.Bytes, 0, 1024)).Concat(f.Bytes[1024..]).ToArray();
        }
        else
        {
            await VideoEncryptionService.EncryptFileAsync(f.OriginalPath, encrypted);
            legacy = await File.ReadAllBytesAsync(encrypted);
        }
        await File.WriteAllBytesAsync(encrypted, legacy);
        File.Delete(f.OriginalPath);
        f.Job.VideoKind = "private-a";
        f.Job.DownloadedBytes = f.Bytes.Length;
        f.Kinds.LabelOf("private-a").Returns("隐私A");
        f.Kinds.VerifyPasswordAsync("private-a", "correct", Arg.Any<CancellationToken>()).Returns(true);
        await using var host = new LocalLibraryHost(f.Repository, f.Engine, f.Kinds, null!, NullLogger<LocalLibraryHost>.Instance);
        using var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        port.Start();
        var number = ((System.Net.IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{number}/");
        listener.Start();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() })
            { BaseAddress = new Uri($"http://127.0.0.1:{number}/"), Timeout = TimeSpan.FromSeconds(10) };
        async Task<HttpResponseMessage> Send(string path, string? json = null, string? range = null, HttpClient? requester = null)
        {
            using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, path);
            if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
            var pending = (requester ?? client).SendAsync(request);
            var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var handler = typeof(LocalLibraryHost).GetMethod("HandleAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)handler.Invoke(host, [ctx])!;
            return await pending;
        }
        async Task<System.Text.Json.JsonDocument> Groups()
        {
            using var response = await Send("/api/videos?group=kind");
            response.EnsureSuccessStatusCode();
            return System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        using (var locked = await Groups())
            Assert.Empty(locked.RootElement[0].GetProperty("items").EnumerateArray());
        using (var wrong = await Send("/api/privacy/unlock", """{"kindValue":"private-a","password":"wrong"}"""))
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, wrong.StatusCode);
        string oldCookie;
        using (var unlock = await Send("/api/privacy/unlock", """{"kindValue":"private-a","password":"correct"}"""))
        {
            unlock.EnsureSuccessStatusCode();
            oldCookie = unlock.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        }
        using (var shown = await Groups())
        {
            var group = shown.RootElement[0];
            Assert.True(group.GetProperty("isUnlocked").GetBoolean());
            Assert.Single(group.GetProperty("items").EnumerateArray());
            Assert.True(group.GetProperty("items")[0].GetProperty("isEncrypted").GetBoolean());
        }
        Assert.Equal(encrypted, f.Job.TargetPath);
        Assert.Equal(encrypted, f.SavedPath);
        Assert.Equal("sample.mp4", f.Job.OriginalFilename);
        Assert.Equal(legacy, await File.ReadAllBytesAsync(encrypted));
        using (var player = await Send($"/play/{f.Job.Id:N}")) player.EnsureSuccessStatusCode();
        using (var item = await Send($"/api/item?id={f.Job.Id}"))
        {
            using var data = System.Text.Json.JsonDocument.Parse(await item.Content.ReadAsStringAsync());
            Assert.True(data.RootElement.GetProperty("isUnlocked").GetBoolean());
        }
        using (var stream = await Send($"/api/stream/{f.Job.Id:N}"))
        {
            stream.EnsureSuccessStatusCode();
            Assert.Equal(f.Bytes, await stream.Content.ReadAsByteArrayAsync());
        }
        using (var seek = await Send($"/api/stream/{f.Job.Id:N}", range: "bytes=1000-1100"))
        {
            Assert.Equal(System.Net.HttpStatusCode.PartialContent, seek.StatusCode);
            Assert.Equal(f.Bytes[1000..1101], await seek.Content.ReadAsByteArrayAsync());
        }
        using (var suffix = await Send($"/api/stream/{f.Job.Id:N}", range: "bytes=-100"))
        {
            Assert.Equal(System.Net.HttpStatusCode.PartialContent, suffix.StatusCode);
            Assert.Equal(f.Bytes[^100..], await suffix.Content.ReadAsByteArrayAsync());
        }
        using (var invalid = await Send($"/api/stream/{f.Job.Id:N}", range: "bytes=99999-"))
            Assert.Equal(System.Net.HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        using (var relock = await Send("/api/privacy/lock", """{"kindValue":"private-a"}"""))
            relock.EnsureSuccessStatusCode();
        using (var hidden = await Groups())
        {
            Assert.False(hidden.RootElement[0].GetProperty("isUnlocked").GetBoolean());
            Assert.Empty(hidden.RootElement[0].GetProperty("items").EnumerateArray());
        }
        var cookies = new System.Net.CookieContainer();
        cookies.SetCookies(client.BaseAddress!, oldCookie);
        using var replay = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = client.BaseAddress };
        using (var denied = await Send($"/api/stream/{f.Job.Id:N}", requester: replay))
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task RecoveryDoesNotGuessBetweenMultipleEncryptedFiles()
    {
        using var f = await Fixture.Create();
        File.Delete(f.OriginalPath);
        f.Job.VideoKind = "private-a";
        f.Job.DownloadedBytes = f.Bytes.Length;
        var encrypted = new byte[f.Bytes.Length + 21];
        "VDENC"u8.CopyTo(encrypted);
        await File.WriteAllBytesAsync(Path.Combine(f.Root, "0123456789abcdef.mp4"), encrypted);
        await File.WriteAllBytesAsync(Path.Combine(f.Root, "fedcba9876543210.mp4"), encrypted);
        await f.Engine.RepairPrivateLibraryAsync("private-a");
        Assert.Equal(f.OriginalPath, f.Job.TargetPath);
        Assert.Equal(f.OriginalPath, f.SavedPath);
    }

    [Fact]
    public async Task RecoverySaveFailureRestoresTrackedJobAndPreservesEncryptedFile()
    {
        using var f = await Fixture.Create();
        var encrypted = Path.Combine(f.Root, "0123456789abcdef.mp4");
        await VideoEncryptionService.EncryptFileAsync(f.OriginalPath, encrypted);
        var before = await File.ReadAllBytesAsync(encrypted);
        File.Delete(f.OriginalPath);
        f.Job.VideoKind = "private-a";
        f.Job.DownloadedBytes = f.Bytes.Length;
        f.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Engine.RepairPrivateLibraryAsync("private-a"));
        Assert.Equal(f.OriginalPath, f.Job.TargetPath);
        Assert.False(f.Job.IsEncrypted);
        Assert.Null(f.Job.OriginalFilename);
        Assert.Equal(before, await File.ReadAllBytesAsync(encrypted));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(15)] [InlineData(16)]
    [InlineData(1023)] [InlineData(1024)] [InlineData(1025)] [InlineData(8192)]
    public async Task FixedKey_HeaderAndPlaybackRoundTripPreserveEveryByte(int length)
    {
        var root = Path.Combine(Path.GetTempPath(), "vd-privacy-crypto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = RandomNumberGenerator.GetBytes(length);
            var source = Path.Combine(root, "original.mp4");
            var cipher = Path.Combine(root, "cipher.mp4");
            var restored = Path.Combine(root, "restored.mp4");
            await File.WriteAllBytesAsync(source, original);
            await VideoEncryptionService.EncryptFileAsync(source, cipher);
            Assert.True(VideoEncryptionService.IsEncryptedFile(cipher));
            var encoded = await File.ReadAllBytesAsync(cipher);
            if (length > 1024)
                Assert.Equal(original[1024..], encoded[^(length - 1024)..]);
            using (var playback = VideoEncryptionService.OpenDecryptedRead(cipher))
            {
                Assert.Equal(length, playback.Length);
                using var bytes = new MemoryStream();
                await playback.CopyToAsync(bytes);
                Assert.Equal(original, bytes.ToArray());
                if (length > 0)
                {
                    playback.Seek(Math.Min(1000, length - 1), SeekOrigin.Begin);
                    using var suffix = new MemoryStream();
                    await playback.CopyToAsync(suffix);
                    Assert.Equal(original[Math.Min(1000, length - 1)..], suffix.ToArray());
                }
            }
            await VideoEncryptionService.DecryptFileAsync(cipher, restored);
            Assert.Equal(original, await File.ReadAllBytesAsync(restored));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CategoryRoundTrip_UpdatesTrackedJobAndSavedCategoryAndRestoresFile()
    {
        using var f = await Fixture.Create();
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        var encrypted = f.Job.TargetPath;
        Assert.True(f.Job.IsEncrypted);
        Assert.Equal("sample.mp4", f.Job.OriginalFilename);
        Assert.Matches("^[0-9a-f]{16}\\.mp4$", Path.GetFileName(encrypted));
        Assert.False(File.Exists(f.OriginalPath));
        Assert.Equal("private-a", f.SavedKind);
        Assert.Equal(encrypted, f.SavedPath);
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "normal");
        Assert.False(f.Job.IsEncrypted);
        Assert.Null(f.Job.OriginalFilename);
        Assert.Equal(f.OriginalPath, f.Job.TargetPath);
        Assert.False(File.Exists(encrypted));
        Assert.Equal(f.Bytes, await File.ReadAllBytesAsync(f.OriginalPath));
        Assert.Equal("normal", f.SavedKind);
        Assert.Same(f.Job, f.Engine.GetActiveJobs().Single());
    }

    [Fact]
    public async Task StartupFilenameBackfillPreservesPrivateOriginalFilename()
    {
        using var f = await Fixture.Create();
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        var cipher = f.Job.TargetPath;
        var original = f.Job.OriginalFilename;
        var name = f.Job.DisplayName;
        var backfill = typeof(DownloadEngine).GetMethod("BackfillCompletedFileNamesAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)backfill.Invoke(f.Engine, [CancellationToken.None])!;
        Assert.Equal(original, f.Job.OriginalFilename);
        Assert.Equal(name, f.Job.DisplayName);
        Assert.Equal(cipher, f.Job.TargetPath);
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "");
        Assert.Equal(f.OriginalPath, f.Job.TargetPath);
        Assert.Equal("sample", f.Job.DisplayName);
        Assert.Equal(f.Bytes, await File.ReadAllBytesAsync(f.OriginalPath));
    }

    [Fact]
    public async Task UnprotectRestoresOriginalFilenameAndQueueDisplayName()
    {
        using var f = await Fixture.Create();
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        var encrypted = f.Job.TargetPath;
        f.Job.DisplayName = Path.GetFileNameWithoutExtension(encrypted);

        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "");

        Assert.Equal(f.OriginalPath, f.Job.TargetPath);
        Assert.Equal("sample.mp4", Path.GetFileName(f.Job.TargetPath));
        Assert.Equal("sample", f.Job.DisplayName);
        Assert.Equal("sample", f.SavedDisplayName);
        Assert.Equal(f.OriginalPath, f.SavedPath);
        Assert.Equal(f.Bytes, await File.ReadAllBytesAsync(f.Job.TargetPath));
        Assert.False(File.Exists(encrypted));
        Assert.False(f.Job.IsEncrypted);
        Assert.Equal("", f.Job.VideoKind);
    }

    [Fact]
    public async Task PrivateToPrivate_KeepsCipherBytesAndUsesSameKeyWhenLeaving()
    {
        using var f = await Fixture.Create();
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        var cipher = f.Job.TargetPath;
        var bytes = await File.ReadAllBytesAsync(cipher);
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-b");
        Assert.Equal(cipher, f.Job.TargetPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cipher));
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "");
        Assert.Equal(f.Bytes, await File.ReadAllBytesAsync(f.Job.TargetPath));
    }

    [Fact]
    public async Task EditingPrivateTitle_KeepsObfuscatedPathAndRestoresUpdatedName()
    {
        using var f = await Fixture.Create();
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        var cipher = f.Job.TargetPath;
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, titleHead: "renamed");
        Assert.Equal(cipher, f.Job.TargetPath);
        Assert.Equal("renamed.mp4", f.Job.OriginalFilename);
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "normal");
        Assert.Equal("renamed.mp4", Path.GetFileName(f.Job.TargetPath));
        Assert.Equal(f.Bytes, await File.ReadAllBytesAsync(f.Job.TargetPath));
    }

    [Fact]
    public async Task QueueRename_DoesNotExposePrivateFilename()
    {
        using var f = await Fixture.Create();
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        var cipher = f.Job.TargetPath;
        await f.Engine.RenameAsync(f.Job.Id, "queue-name");
        Assert.Equal(cipher, f.Job.TargetPath);
        Assert.Equal("queue-name.mp4", f.Job.OriginalFilename);
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "normal");
        Assert.Equal("queue-name.mp4", Path.GetFileName(f.Job.TargetPath));
        Assert.Equal(f.Bytes, await File.ReadAllBytesAsync(f.Job.TargetPath));
    }

    [Fact]
    public async Task LeavingPrivateWhilePlaybackIsOpen_RestoresFileAndPreservesCurrentRead()
    {
        using var f = await Fixture.Create();
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        using var playback = VideoEncryptionService.OpenDecryptedRead(f.Job.TargetPath);
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "normal");
        using var bytes = new MemoryStream();
        await playback.CopyToAsync(bytes);
        Assert.Equal(f.Bytes, bytes.ToArray());
        Assert.Equal(f.Bytes, await File.ReadAllBytesAsync(f.Job.TargetPath));
    }

    [Fact]
    public async Task RestoreCollision_PreservesCipherCategoryAndUnrelatedFile()
    {
        using var f = await Fixture.Create();
        await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        var cipher = f.Job.TargetPath;
        await File.WriteAllTextAsync(f.OriginalPath, "unrelated");
        await Assert.ThrowsAsync<IOException>(() => f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "normal"));
        Assert.Equal("private-a", f.Job.VideoKind);
        Assert.Equal(cipher, f.Job.TargetPath);
        Assert.True(f.Job.IsEncrypted);
        Assert.Equal("unrelated", await File.ReadAllTextAsync(f.OriginalPath));
        Assert.Equal(2, Directory.GetFiles(f.Root).Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DatabaseFailure_RetainsSourceAndRestoresInMemoryMetadata(bool decrypt)
    {
        using var f = await Fixture.Create();
        if (decrypt) await f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a");
        var path = f.Job.TargetPath;
        var sourceBytes = await File.ReadAllBytesAsync(path);
        f.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Engine.UpdateLibraryItemAsync(f.Job.Id,
            videoKind: decrypt ? "normal" : "private-a"));
        Assert.Equal(path, f.Job.TargetPath);
        Assert.Equal(decrypt, f.Job.IsEncrypted);
        Assert.Equal(decrypt ? "private-a" : "normal", f.Job.VideoKind);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(f.Root));
    }

    [Fact]
    public async Task LegacyFormat_IsNotMigratedOrDeletedOnCategoryChange()
    {
        using var f = await Fixture.Create();
        await File.WriteAllBytesAsync(f.OriginalPath, "VDENClegacy-format-not-supported"u8.ToArray());
        f.Job.VideoKind = "private-a";
        f.Job.EncryptedFilename = "sample.mp4";
        f.Job.OriginalFilename = "restored.mp4";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "normal"));
        Assert.True(f.Job.IsEncrypted);
        Assert.Equal("private-a", f.Job.VideoKind);
        Assert.True(File.Exists(f.OriginalPath));
        Assert.Single(Directory.GetFiles(f.Root));
    }

    [Fact]
    public async Task SourceCannotBeDeleted_RollsBackSavedCategoryAndKeepsOriginal()
    {
        using var f = await Fixture.Create();
        using var reader = new FileStream(f.OriginalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAnyAsync<IOException>(() => f.Engine.UpdateLibraryItemAsync(f.Job.Id, videoKind: "private-a"));
        Assert.Equal("normal", f.Job.VideoKind);
        Assert.Equal("normal", f.SavedKind);
        Assert.Equal(f.OriginalPath, f.Job.TargetPath);
        Assert.False(f.Job.IsEncrypted);
        Assert.Equal(f.Bytes, await File.ReadAllBytesAsync(f.OriginalPath));
        Assert.Single(Directory.GetFiles(f.Root));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vd-privacy-edit-" + Guid.NewGuid().ToString("N"));
        public string OriginalPath => Path.Combine(Root, "sample.mp4");
        public byte[] Bytes { get; } = RandomNumberGenerator.GetBytes(4096);
        public DownloadJob Job { get; private set; } = null!;
        public DownloadEngine Engine { get; private set; } = null!;
        public IDownloadRepository Repository { get; private set; } = null!;
        public ILibraryKindCatalog Kinds { get; private set; } = null!;
        public bool FailSaves;
        public string? SavedKind;
        public string? SavedPath;
        public string? SavedDisplayName;
        private readonly HttpClient _http = new();

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Root);
            await File.WriteAllBytesAsync(f.OriginalPath, f.Bytes);
            f.Job = new DownloadJob { Id = Guid.NewGuid(), DisplayName = "sample", TargetPath = f.OriginalPath,
                VideoKind = "normal", Status = DownloadStatus.Completed };
            var repo = Substitute.For<IDownloadRepository>();
            f.Repository = repo;
            repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DownloadJob>>([f.Job]));
            repo.GetByIdAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<DownloadJob?>(f.Job));
            repo.SaveAsync(Arg.Any<DownloadJob>(), Arg.Any<CancellationToken>()).Returns(ci =>
            {
                if (f.FailSaves) throw new IOException("Injected database failure");
                var job = ci.ArgAt<DownloadJob>(0);
                f.SavedKind = job.VideoKind;
                f.SavedPath = job.TargetPath;
                f.SavedDisplayName = job.DisplayName;
                return Task.CompletedTask;
            });
            var kinds = Substitute.For<ILibraryKindCatalog>();
            f.Kinds = kinds;
            LibraryKindEntry[] entries = [new("", "未分类", 0, true), new("normal", "普通", 1, true),
                new("private-a", "隐私A", 2, false, true), new("private-b", "隐私B", 3, false, true)];
            kinds.GetAll().Returns(entries);
            kinds.IsKnown(Arg.Any<string?>()).Returns(ci => entries.Any(k => k.Value == (ci.ArgAt<string?>(0) ?? "")));
            kinds.Normalize(Arg.Any<string?>()).Returns(ci => ci.ArgAt<string?>(0) ?? "");
            var options = Options.Create(new AppOptions { Download = new DownloadOptions {
                DefaultSavePath = f.Root, AutoRecoverDownloads = false, FailedRetryIntervalSeconds = 0 } });
            var request = new RequestMessageFactory(options);
            var ffmpeg = Substitute.For<IFfmpegAdapter>();
            f.Engine = new DownloadEngine(new HttpMediaDownloader(f._http, request, options, NullLogger<HttpMediaDownloader>.Instance),
                new M3u8DownloadAdapter(request, ffmpeg, Substitute.For<IHttpClientFactory>()), ffmpeg,
                new DownloadJobStateMachine(), repo, Substitute.For<IRequestContextProvider>(), new DownloadBackendRouter(),
                options, NullLogger<DownloadEngine>.Instance,
                new LicenseService(f._http, options, NullLogger<LicenseService>.Instance),
                new LocalVideoThumbnailStore(ffmpeg, NullLogger<LocalVideoThumbnailStore>.Instance), kinds);
            await f.Engine.RecoverOnStartupAsync();
            return f;
        }

        public void Dispose()
        {
            Engine?.Dispose();
            _http.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
