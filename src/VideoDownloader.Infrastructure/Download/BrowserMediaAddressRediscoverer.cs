using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Browser;

namespace VideoDownloader.Infrastructure.Download;

/// <summary>
/// Navigates the active WebView to a stable recovery page and waits for a durable media address.
/// </summary>
public sealed class BrowserMediaAddressRediscoverer : IMediaAddressRediscoverer
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(45);

    private readonly BrowserHostLocator _locator;
    private readonly IMediaDetectionPipeline _pipeline;
    private readonly ILogger<BrowserMediaAddressRediscoverer> _logger;

    public BrowserMediaAddressRediscoverer(
        BrowserHostLocator locator,
        IMediaDetectionPipeline pipeline,
        ILogger<BrowserMediaAddressRediscoverer> logger)
    {
        _locator = locator;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<MediaVariant?> RediscoverAsync(
        Uri recoveryPage,
        MediaVariant previous,
        CancellationToken ct)
    {
        var host = _locator.Active;
        if (host is null)
        {
            _logger.LogWarning("Browser rediscover skipped: no active WebView host");
            return null;
        }

        var target = MediaAddressRenewal.HasStableContentAddress(recoveryPage)
            ? recoveryPage
            : MediaAddressRenewal.RecoveryAddress(recoveryPage, previous.ContentIdentity) ?? recoveryPage;
        if (!MediaAddressRenewal.HasStableContentAddress(target))
        {
            _logger.LogWarning(
                "Browser rediscover skipped: unstable recovery page={Page}",
                recoveryPage.AbsolutePath);
            return null;
        }

        var previousUrl = host.CurrentPageUrl;
        var tcs = new TaskCompletionSource<MediaVariant>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnVideo(object? sender, DetectedVideo video)
        {
            if (!MatchesContent(previous, video))
                return;
            var pick = SelectDurableVariant(video, previous);
            if (pick is null)
                return;
            tcs.TrySetResult(pick with
            {
                ContentIdentity = previous.ContentIdentity ?? pick.ContentIdentity,
                RecoveryPageUrl = target
            });
        }

        _pipeline.VideoDetected += OnVideo;
        _pipeline.VideoUpdated += OnVideo;
        try
        {
            _logger.LogInformation(
                "Douyin browser rediscover begin page={Path} identity={Identity}",
                target.AbsolutePath,
                previous.ContentIdentity);
            await host.NavigateAsync(target.AbsoluteUri, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WaitBudget);
            try
            {
                // Nudge observation after navigation settles.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500, timeout.Token).ConfigureAwait(false);
                        await host.ProbeCurrentPageAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Browser rediscover probe nudge failed");
                    }
                }, timeout.Token);

                var found = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                _logger.LogInformation(
                    "Douyin browser rediscover ok host={Host} identity={Identity}",
                    found.SourceUrl.Host,
                    found.ContentIdentity);
                return found;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Douyin browser rediscover timed out page={Path} identity={Identity}",
                    target.AbsolutePath,
                    previous.ContentIdentity);
                return null;
            }
        }
        finally
        {
            _pipeline.VideoDetected -= OnVideo;
            _pipeline.VideoUpdated -= OnVideo;
            if (previousUrl is not null &&
                !string.Equals(previousUrl.AbsoluteUri, target.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await host.NavigateAsync(previousUrl.AbsoluteUri, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Browser rediscover failed to restore previous page");
                }
            }
        }
    }

    private static bool MatchesContent(MediaVariant previous, DetectedVideo video)
    {
        if (previous.ContentIdentity is { Length: > 0 } identity &&
            identity.StartsWith("id:", StringComparison.Ordinal) &&
            video.SiteContentId is { Length: > 0 } siteId)
        {
            return string.Equals(identity, "id:" + siteId, StringComparison.Ordinal);
        }

        if (previous.RecoveryPageUrl is not null &&
            string.Equals(
                previous.RecoveryPageUrl.AbsoluteUri,
                video.PageUrl.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase))
            return true;

        return video.Variants.Any(v =>
            previous.ContentIdentity is { Length: > 0 } &&
            string.Equals(v.ContentIdentity, previous.ContentIdentity, StringComparison.Ordinal));
    }

    private static MediaVariant? SelectDurableVariant(DetectedVideo video, MediaVariant previous)
    {
        return video.Variants
            .Where(v => !v.Tracks.Any(t => t.IsMseTrack))
            .Where(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Combined or MediaTrackKind.Video))
            .Where(v => MediaAddressRenewal.Compatible(previous, v))
            .Where(v => !MediaAddressRenewal.IsKnownUndersizedVideo(v))
            .Where(HasUsablePrimary)
            .OrderByDescending(DurableScore)
            .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
            .FirstOrDefault();
    }

    private static bool HasUsablePrimary(MediaVariant variant)
    {
        var primary = variant.Tracks.FirstOrDefault(t =>
            t.Kind is MediaTrackKind.Combined or MediaTrackKind.Video);
        if (primary is null)
            return false;
        // Accept durable hosts, or fragile only when already verified/validated.
        if (!MediaAddressRenewal.IsFragileSignedHost(primary.SourceUrl))
            return true;
        return primary.IsValidated ||
               (primary.BrowserObserved && primary.Evidence == MediaEvidence.BrowserObserved);
    }

    private static int DurableScore(MediaVariant variant)
    {
        var host = variant.SourceUrl.Host;
        if (host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase)) return 300;
        if (MediaAddressRenewal.IsFragileSignedHost(variant.SourceUrl)) return -400;
        return MediaAddressRenewal.DurableHostScore(variant);
    }
}
