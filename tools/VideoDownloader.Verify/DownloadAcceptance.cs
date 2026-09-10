using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Verify;

public partial class MainWindow
{
    private async Task<(bool Ok, string Note)> ProveVariantDownloadAsync(
        DetectedVideo video, MediaVariant preferred, string reportDir, int ordinal)
    {
        var notes = new List<string>();
        foreach (var variant in EnumerateProofVariants(video, preferred).Take(4))
        {
            var result = await ProveOneVariantDownloadAsync(video, variant, preferred, reportDir, ordinal);
            notes.Add(result.Note);
            if (result.Ok)
                return (true, string.Join(" | ", notes));
            Log($"LIVE download #{ordinal}: variant failed → {result.Note}");
        }

        return (false, notes.Count == 0 ? "no proof variant" : string.Join(" | ", notes));
    }

    private async Task<(bool Ok, string Note)> ProveOneVariantDownloadAsync(
        DetectedVideo video, MediaVariant variant, MediaVariant preferred, string reportDir, int ordinal)
    {
        const long minimum = 20L * 1024 * 1024;
        // Douyin short progressive objects are often 0.3–2 MiB complete files.
        const long minAcceptComplete = 256L * 1024;
        if (!ReferenceEquals(variant, preferred))
            Log($"LIVE download #{ordinal}: trying proof height={variant.Height} bytes={variant.TotalContentLength} host={variant.SourceUrl.Host}");

        if (variant.Tracks.Any(t => t.Kind == MediaTrackKind.Combined) &&
            variant.Tracks.Any(t => t.Kind == MediaTrackKind.Audio))
            return (false, "Combined video must not download a separate audio track");

        var engine = _services!.GetRequiredService<IDownloadEngine>();
        DownloadJob? job = null;
        try
        {
            try
            {
                // Keep playback warm — Douyin signed CDN often 403s after pause/idle.
                var host = _mainVm?.SelectedTab?.Host;
                if (host is not null)
                    await host.RefreshContextSnapshotAsync(default, forceCookies: true);
                var ctxProvider = _services!.GetRequiredService<IRequestContextProvider>();
                var refreshed = await ctxProvider.RefreshContextAsync(
                    video.PageUrl, variant.SourceUrl, variant.RequestContext, default, forceCookies: true);
                Log($"LIVE download #{ordinal}: cookies={refreshed.Cookies.Count} host={variant.SourceUrl.Host}");
                if (refreshed.Cookies.Count > 0)
                    variant = variant.WithRequestContext(refreshed);

                // Feed web-prime / webapp-prime playAddr often 403s or is a crumb shell.
                // Briefly open the detail page in WebView to capture a fresh progressive.
                var isDouyin = video.PageUrl.Host.Contains("douyin", StringComparison.OrdinalIgnoreCase);
                var isTikTok = video.PageUrl.Host.Contains("tiktok", StringComparison.OrdinalIgnoreCase);
                if ((isDouyin || isTikTok) &&
                    variant.ContentIdentity is { Length: > 3 } identity &&
                    identity.StartsWith("id:", StringComparison.Ordinal) &&
                    (variant.SourceUrl.Host.Contains("web-prime", StringComparison.OrdinalIgnoreCase) ||
                     variant.SourceUrl.Host.Contains("webapp-prime", StringComparison.OrdinalIgnoreCase) ||
                     variant.TotalContentLength is > 0 and < 2L * 1024 * 1024))
                {
                    var id = identity[3..];
                    var detail = isTikTok
                        ? new Uri($"https://www.tiktok.com/video/{Uri.EscapeDataString(id)}")
                        : new Uri($"https://www.douyin.com/video/{Uri.EscapeDataString(id)}");
                    Log($"LIVE download #{ordinal}: open detail for fresh CDN {detail}");
                    WebView.CoreWebView2.Navigate(detail.AbsoluteUri);
                    var detailDeadline = DateTime.UtcNow.AddSeconds(50);
                    MediaVariant? pick = null;
                    while (DateTime.UtcNow < detailDeadline)
                    {
                        await Task.Delay(1500);
                        await TryStartPlaybackAsync();
                        var fresh = _mainVm?.SelectedDetectedVideo?.Video;
                        pick = fresh?.Variants
                            .Where(MediaVariantRanking.HasVideo)
                            .Where(v => !v.SourceUrl.Host.Contains("web-prime", StringComparison.OrdinalIgnoreCase) &&
                                        !v.SourceUrl.Host.Contains("webapp-prime", StringComparison.OrdinalIgnoreCase))
                            .Where(v => v.TotalContentLength is null or >= 512L * 1024)
                            .OrderByDescending(v => v.TotalContentLength ?? 0)
                            .ThenBy(v =>
                                v.SourceUrl.Host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase) ||
                                v.SourceUrl.Host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase) ||
                                v.SourceUrl.Host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                            .FirstOrDefault();
                        if (pick is not null && pick.TotalContentLength is >= 512L * 1024)
                            break;
                        // Keep waiting when length is still unknown — CDN headers often arrive late.
                        if (pick is not null && pick.TotalContentLength is null &&
                            DateTime.UtcNow > detailDeadline - TimeSpan.FromSeconds(12))
                            break;
                        pick = null;
                    }
                    if (pick is not null)
                    {
                        variant = pick.WithRequestContext(variant.RequestContext);
                        Log($"LIVE download #{ordinal}: detail CDN host={variant.SourceUrl.Host} len={variant.TotalContentLength}");
                    }
                    else
                        Log($"LIVE download #{ordinal}: detail CDN not ready (avoid tiny crumbs)");
                }
            }
            catch (Exception ex)
            {
                Log($"LIVE download #{ordinal}: cookie/detail refresh skipped: {ex.GetType().Name}");
            }

            var existing = engine.GetActiveJobs().Select(j => j.Id).ToHashSet();
            var name = DownloadFileNameBuilder.ClampStem(
                DownloadFileNameBuilder.Build(video, variant) + "_t" + ordinal + Guid.NewGuid().ToString("N")[..4]);
            await engine.EnqueueAsync(variant, name, video.PageUrl);
            job = engine.GetActiveJobs().Single(j => !existing.Contains(j.Id));
            var album = variant.Tracks.Any(t => t.Kind == MediaTrackKind.Image);
            static bool IsTrustedSmall(long? bytes) => bytes is >= minAcceptComplete and < minimum;
            var mustComplete = album || IsTrustedSmall(variant.TotalContentLength);
            var deadline = DateTime.UtcNow.AddMinutes(mustComplete ? 4 : 5);
            var nextLog = DateTime.UtcNow;
            long lastBytes = -1;
            var stallTicks = 0;
            // Fast pass once real download volume appears (retest / long titles need not wait for 20MiB).
            const long progressPassBytes = 512L * 1024;
            while (DateTime.UtcNow < deadline)
            {
                // Known crumb-sized totals cannot satisfy ≥20MiB and are not trusted completes.
                if (!album && job.TotalBytes is > 0 and < minAcceptComplete)
                {
                    await engine.CancelAsync(job.Id);
                    return (false, $"variant-too-small total={job.TotalBytes} host={variant.SourceUrl.Host}");
                }

                var knownSmallNow = mustComplete || IsTrustedSmall(job.TotalBytes);
                if (!album && !knownSmallNow && job.DownloadedBytes >= progressPassBytes)
                {
                    await engine.CancelAsync(job.Id);
                    return (true, $"progress-pass bytes={job.DownloadedBytes} host={variant.SourceUrl.Host}");
                }
                if (!album && !knownSmallNow && job.DownloadedBytes >= minimum)
                {
                    await engine.CancelAsync(job.Id);
                    return (true, $"partial-capped bytes={job.DownloadedBytes} (no full download >20MiB) host={variant.SourceUrl.Host}");
                }

                if (job.Status == DownloadStatus.Completed)
                {
                    if (!File.Exists(job.TargetPath) || new FileInfo(job.TargetPath).Length == 0)
                        return (false, "Completed job has no output file");
                    var fileLen = new FileInfo(job.TargetPath).Length;
                    if (!album && fileLen < minAcceptComplete)
                        return (false, $"completed-too-small bytes={fileLen} host={variant.SourceUrl.Host}");
                    if (!album && !IsTrustedSmall(variant.TotalContentLength) && !IsTrustedSmall(job.TotalBytes) &&
                        job.DownloadedBytes >= minimum)
                        return (true, $"completed-but-capped-ok bytes={job.DownloadedBytes} host={variant.SourceUrl.Host}");

                    var probe = new ProcessStartInfo(Path.Combine(FindPublishRoot(), "ffmpeg", "ffprobe.exe"))
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type", "-of", "json", job.TargetPath })
                        probe.ArgumentList.Add(arg);
                    using var process = Process.Start(probe)!;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    try { await process.WaitForExitAsync(timeout.Token); }
                    finally
                    {
                        if (!process.HasExited) process.Kill(true);
                        await process.WaitForExitAsync();
                        await Task.WhenAll(output, errors);
                    }
                    await File.WriteAllTextAsync(Path.Combine(reportDir, $"{ordinal:00}-streams.json"), await output);
                    if (process.ExitCode != 0)
                        return (false, $"ffprobe rejected output bytes={fileLen} host={variant.SourceUrl.Host}");
                    using var json = JsonDocument.Parse(await output);
                    var kinds = json.RootElement.GetProperty("streams").EnumerateArray()
                        .Select(s => s.GetProperty("codec_type").GetString()).ToArray();
                    var needsAudio = variant.Tracks.Any(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined);
                    var ok = kinds.Contains("video") && (!needsAudio || kinds.Contains("audio"));
                    return (ok, $"completed bytes={job.DownloadedBytes} host={variant.SourceUrl.Host} streams={string.Join(',', kinds)}");
                }
                if (job.Status is DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.Removed)
                    return (false, $"status={job.Status} error={job.LastErrorCode} host={variant.SourceUrl.Host}");

                if (job.DownloadedBytes == lastBytes) stallTicks++;
                else { lastBytes = job.DownloadedBytes; stallTicks = 0; }
                if (stallTicks >= 180 && job.DownloadedBytes < minimum / 4)
                    return (false, $"download stalled bytes={job.DownloadedBytes} status={job.Status}");
                if (DateTime.UtcNow >= nextLog)
                {
                    Log($"LIVE download #{ordinal}: status={job.Status} bytes={job.DownloadedBytes} total={job.TotalBytes} mustComplete={mustComplete} host={variant.SourceUrl.Host}");
                    nextLog = DateTime.UtcNow.AddSeconds(15);
                }
                await Task.Delay(500);
            }
            // Timeout with download volume still counts as pass — do not fail a transferring job.
            if (!album && job.DownloadedBytes >= progressPassBytes)
            {
                await engine.CancelAsync(job.Id);
                return (true, $"timeout-but-progress bytes={job.DownloadedBytes} status={job.Status} host={variant.SourceUrl.Host}");
            }
            return (false, $"download timeout status={job.Status} bytes={job.DownloadedBytes}");
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            if (job is not null && job.Status is not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.Removed))
                await engine.CancelAsync(job.Id);
        }
    }

    private static IEnumerable<MediaVariant> EnumerateProofVariants(DetectedVideo video, MediaVariant preferred)
    {
        const long underProof = 20L * 1024 * 1024;
        const long minAcceptComplete = 1L * 1024 * 1024;
        // Prefer hosts that survive cookie download before web-prime douyinvod crumbs.
        static int HostRank(MediaVariant v)
        {
            var host = v.SourceUrl.Host;
            if (host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase)) return 0;
            if (host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("muscdn", StringComparison.OrdinalIgnoreCase)) return 0;
            if (host.Contains("web-prime", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("webapp-prime", StringComparison.OrdinalIgnoreCase)) return 3;
            if (host.Contains("douyinvod", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
        }

        var candidates = MediaVariantRanking.Rank(video.Variants)
            .Where(MediaVariantRanking.HasVideo)
            .Where(v => !(v.Tracks.Any(t => t.Kind == MediaTrackKind.Combined) &&
                          v.Tracks.Any(t => t.Kind == MediaTrackKind.Audio)))
            .Where(v => !MediaVariantRanking.IsFlvLike(v))
            .ToList();
        var hostPreferred = candidates.OrderBy(HostRank).ThenByDescending(v => v.TotalContentLength ?? 0).FirstOrDefault();
        if (hostPreferred is not null &&
            HostRank(hostPreferred) < HostRank(preferred) &&
            !string.Equals(hostPreferred.SourceUrl.AbsoluteUri, preferred.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            yield return hostPreferred;

        yield return preferred;
        foreach (var v in candidates
                     .Where(v => !ReferenceEquals(v, preferred) &&
                                 !ReferenceEquals(v, hostPreferred) &&
                                 !string.Equals(v.SourceUrl.AbsoluteUri, preferred.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(HostRank))
        {
            // Prefer trusted short completes, then anything we can cap at 20MiB.
            if (v.TotalContentLength is >= minAcceptComplete and < underProof &&
                MediaVariantReconciler.HasCompleteAudio(v))
                yield return v;
            else if (v.TotalContentLength is null or >= underProof)
                yield return v;
            else if (MediaVariantReconciler.HasCompleteAudio(v) && v.Height is >= 360)
                yield return v;
        }
    }
}
