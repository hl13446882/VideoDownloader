using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Http;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>
/// Bounded sample validation + same-content recovery during probe (T1/T2/C1).
/// </summary>
internal static class ProbeSampleGate
{
    private const int MaxRecoveryResolves = 1;
    private const int MaxAlternateTries = 4;

    internal static async Task<(DetectedVideo? Video, string? VideoFailure)> ValidateAndRecoverAsync(
        DetectedVideo video,
        Uri documentPage,
        string? ownership,
        RequestContext context,
        IExternalSiteResolver resolver,
        MediaAvailabilityValidator validator,
        Func<bool> isCurrentSession,
        ILogger logger,
        Action<ProbeCandidateDecision> record,
        CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        var sampleCache = new Dictionary<(Uri, Guid, int), (bool Ok, string? Code)>();

        async Task<(bool Ok, string? Code)> SampleAsync(MediaVariant variant)
        {
            // Browser CDP already proved accessibility; an out-of-band GET often 403s on TikTok.
            if (variant.Tracks.Any(t => t.BrowserObserved))
                return (true, null);

            if (variant.Tracks.Any(t => t.Container is "hls" or "dash"))
                return (true, null);

            var ok = true;
            string? code = null;
            foreach (var track in variant.Tracks)
            {
                var key = (track.SourceUrl, track.RequestContext.ContextId, track.RequestContext.Version);
                if (!sampleCache.TryGetValue(key, out var cached))
                {
                    if (budget.IsCancellationRequested)
                        return (false, ErrorCodes.NetTimeout);
                    try
                    {
                        await validator.ValidateAsync(variant with { Tracks = [track] }, budget.Token);
                        cached = (true, null);
                    }
                    catch (DownloadException ex)
                    {
                        cached = (false, ex.ErrorCode);
                        logger.LogInformation("Probe sample rejected host={Host} kind={Kind} code={Code}",
                            track.SourceUrl.Host, track.Kind, ex.ErrorCode);
                    }
                    catch (HttpRequestException)
                    {
                        cached = (false, "NETWORK");
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        cached = (false, ErrorCodes.NetTimeout);
                    }

                    sampleCache[key] = cached;
                }

                if (!cached.Ok)
                {
                    ok = false;
                    code ??= cached.Code;
                }
            }

            return (ok, code);
        }

        static bool HasVideo(MediaVariant v) =>
            v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined);

        static bool HasAudio(MediaVariant v) =>
            v.Tracks.Any(t => t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined);

        static string KindOf(MediaVariant v) =>
            HasVideo(v) && HasAudio(v) ? "av" : HasVideo(v) ? "video" : "audio";

        var usable = new List<MediaVariant>();
        string? videoFailure = null;
        var videoCandidates = video.Variants.Where(HasVideo).ToList();
        var audioCandidates = video.Variants.Where(v => !HasVideo(v) && HasAudio(v)).ToList();
        var acceptedVideoSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Page-level browser play facts win: accept them without out-of-band sampling,
        // and do not keep probing unmatched yt-dlp CDN objects that typically 403.
        var browserVideos = videoCandidates
            .Where(v => v.Tracks.Any(t => t.BrowserObserved))
            .ToList();
        if (browserVideos.Count > 0)
        {
            foreach (var preferred in browserVideos)
            {
                if (!isCurrentSession())
                    return (null, ErrorCodes.Cancelled);
                if (!acceptedVideoSources.Add(preferred.SourceUrl.AbsoluteUri))
                    continue;
                record(new("external", KindOf(preferred), ownership, "accepted", "browser_observed", preferred.SourceUrl.Host));
                usable.Add(preferred with
                {
                    ContentIdentity = preferred.ContentIdentity ?? ownership,
                    RecoveryPageUrl = preferred.RecoveryPageUrl ??
                                      MediaAddressRenewal.RecoveryAddress(documentPage, ownership) ??
                                      documentPage,
                    Alternatives = videoCandidates
                        .Where(a => a.SourceUrl != preferred.SourceUrl && MediaAddressRenewal.Compatible(preferred, a))
                        .Take(MaxAlternateTries)
                        .ToArray()
                });
            }

            // Skip non-observed video sampling when browser already proved a video.
            videoCandidates = [];
        }

        // Validate preferred video variants; on 403 try same-batch alternates (T1).
        foreach (var preferred in videoCandidates)
        {
            if (!isCurrentSession())
                return (null, ErrorCodes.Cancelled);
            if (acceptedVideoSources.Contains(preferred.SourceUrl.AbsoluteUri))
                continue;

            var (ok, code) = await SampleAsync(preferred);
            if (ok)
            {
                record(new("external", KindOf(preferred), ownership, "accepted", "sample_ok", preferred.SourceUrl.Host));
                usable.Add(preferred with
                {
                    ContentIdentity = preferred.ContentIdentity ?? ownership,
                    RecoveryPageUrl = preferred.RecoveryPageUrl ??
                                      MediaAddressRenewal.RecoveryAddress(documentPage, ownership) ??
                                      documentPage,
                    Alternatives = videoCandidates
                        .Where(a => a.SourceUrl != preferred.SourceUrl && MediaAddressRenewal.Compatible(preferred, a))
                        .Take(MaxAlternateTries)
                        .ToArray()
                });
                acceptedVideoSources.Add(preferred.SourceUrl.AbsoluteUri);
                continue;
            }

            videoFailure ??= code;
            record(new("external", KindOf(preferred), ownership, "rejected", code ?? "sample_failed", preferred.SourceUrl.Host));

            if (code != ErrorCodes.Http403)
                continue;

            var recovered = false;
            foreach (var alternate in videoCandidates
                         .Where(a => a.SourceUrl != preferred.SourceUrl &&
                                     !acceptedVideoSources.Contains(a.SourceUrl.AbsoluteUri) &&
                                     MediaAddressRenewal.Compatible(preferred, a))
                         .Take(MaxAlternateTries))
            {
                if (!isCurrentSession())
                    return (null, ErrorCodes.Cancelled);
                var (altOk, altCode) = await SampleAsync(alternate);
                if (!altOk)
                {
                    record(new("external", KindOf(alternate), ownership, "rejected", altCode ?? "alt_failed", alternate.SourceUrl.Host, "alternate"));
                    continue;
                }

                record(new("external", KindOf(alternate), ownership, "accepted", "alternate_ok", alternate.SourceUrl.Host));
                usable.Add(alternate with
                {
                    ContentIdentity = ownership ?? alternate.ContentIdentity,
                    RecoveryPageUrl = MediaAddressRenewal.RecoveryAddress(documentPage, ownership) ?? documentPage,
                    Alternatives = []
                });
                acceptedVideoSources.Add(alternate.SourceUrl.AbsoluteUri);
                recovered = true;
                break;
            }

            if (!recovered && MaxRecoveryResolves > 0)
            {
                var recoveryPage = preferred.RecoveryPageUrl
                                   ?? MediaAddressRenewal.RecoveryAddress(documentPage, ownership)
                                   ?? (MediaAddressRenewal.HasStableContentAddress(documentPage) ? documentPage : null);
                if (recoveryPage is null)
                {
                    record(new("external", "video", ownership, "rejected", "no_stable_recovery_url", preferred.SourceUrl.Host));
                    continue;
                }

                if (!isCurrentSession())
                    return (null, ErrorCodes.Cancelled);

                IReadOnlyList<DetectedVideo> renewed;
                try
                {
                    renewed = await resolver.ResolveAsync(recoveryPage, context, budget.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    record(new("external", "video", ownership, "rejected", ErrorCodes.NetTimeout, recoveryPage.Host, "renew"));
                    continue;
                }
                catch (Exception ex)
                {
                    record(new("external", "video", ownership, "rejected", "renew_exception", recoveryPage.Host, ex.GetType().Name));
                    continue;
                }

                if (!isCurrentSession())
                    return (null, ErrorCodes.Cancelled);

                var matches = renewed
                    .Where(v => ownership is null || ownership == "id:" + v.SiteContentId ||
                                string.IsNullOrWhiteSpace(v.SiteContentId))
                    .SelectMany(v => v.Variants)
                    .Where(HasVideo)
                    .Where(v => MediaAddressRenewal.Compatible(preferred, v) || preferred.Height is null)
                    .Take(MaxAlternateTries);

                foreach (var match in matches)
                {
                    var (renewOk, renewCode) = await SampleAsync(match);
                    if (!renewOk)
                    {
                        record(new("external", KindOf(match), ownership, "rejected", renewCode ?? "renew_sample_failed", match.SourceUrl.Host, "renew"));
                        continue;
                    }

                    record(new("external", KindOf(match), ownership, "accepted", "renew_ok", match.SourceUrl.Host));
                    usable.Add(match with
                    {
                        ContentIdentity = ownership ?? match.ContentIdentity,
                        RecoveryPageUrl = recoveryPage,
                        Alternatives = []
                    });
                    recovered = true;
                    break;
                }

                if (!recovered)
                    videoFailure ??= ErrorCodes.Http403;
            }
        }

        // Always keep validated audio (T1/T2) — never wipe it because video failed.
        foreach (var audio in audioCandidates)
        {
            if (!isCurrentSession())
                return (null, ErrorCodes.Cancelled);
            var (ok, code) = await SampleAsync(audio);
            if (ok)
            {
                record(new("external", "audio", ownership, "accepted", "sample_ok", audio.SourceUrl.Host));
                usable.Add(audio with
                {
                    ContentIdentity = audio.ContentIdentity ?? ownership,
                    RecoveryPageUrl = audio.RecoveryPageUrl ?? documentPage
                });
            }
            else
            {
                record(new("external", "audio", ownership, "rejected", code ?? "sample_failed", audio.SourceUrl.Host));
            }
        }

        if (usable.Count == 0)
            return (null, videoFailure ?? ErrorCodes.InvalidFormat);

        var hasVideo = usable.Any(HasVideo);
        var hasAudio = usable.Any(HasAudio);
        var availability = hasVideo
            ? MediaAvailabilityKind.Complete
            : hasAudio
                ? (videoFailure == ErrorCodes.Http403
                    ? MediaAvailabilityKind.VideoDenied
                    : MediaAvailabilityKind.AudioOnly)
                : MediaAvailabilityKind.Unavailable;

        var hint = availability switch
        {
            MediaAvailabilityKind.VideoDenied => "仅音轨可用（视频访问被拒绝）",
            MediaAvailabilityKind.AudioOnly => "仅音轨可用",
            _ => video.StatusHint
        };

        var metadata = new Dictionary<string, string>(video.Metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (videoFailure is not null)
            metadata["videoFailure"] = videoFailure;
        metadata["availability"] = availability.ToString();

        return (video with
        {
            Variants = usable,
            Availability = availability,
            StatusHint = hint,
            Metadata = metadata
        }, videoFailure);
    }
}
