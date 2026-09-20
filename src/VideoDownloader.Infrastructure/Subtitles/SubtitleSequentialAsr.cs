using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles;

/// <summary>
/// Front-to-back Whisper windows shared by live playback and idle prewarm.
/// </summary>
internal static class SubtitleSequentialAsr
{
    public static readonly TimeSpan FastWarmupWindow = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan WindowOverlap = TimeSpan.FromSeconds(1.5);

    public static async Task RunAsync(
        string filePath,
        ISubtitlePipeline pipeline,
        IMediaAudioDecoder audioDecoder,
        SubtitleMode mode,
        TimeSpan windowSize,
        TimeSpan? duration,
        Func<TimeSpan?>? liveDuration,
        Func<bool>? shouldContinue,
        ILogger logger,
        string logLabel,
        CancellationToken cancellationToken)
    {
        windowSize = TimeSpan.FromSeconds(Math.Clamp(windowSize.TotalSeconds, 10, 90));

        while (!cancellationToken.IsCancellationRequested)
        {
            if (shouldContinue is not null && !shouldContinue())
                return;

            var total = liveDuration?.Invoke() ?? duration;
            var progress = pipeline.GetSequentialCoveredUntil();
            if (total is { } end && progress + TimeSpan.FromMilliseconds(500) >= end)
            {
                logger.LogInformation(
                    "Subtitle sequential ASR complete label={Label} covered={Covered:g}",
                    logLabel,
                    progress);
                return;
            }

            var start = progress;
            if (start > WindowOverlap)
                start -= WindowOverlap;

            var desiredWindow = progress <= TimeSpan.Zero && windowSize > FastWarmupWindow
                ? FastWarmupWindow
                : windowSize;
            var remaining = total is { } t ? t - start : desiredWindow;
            if (remaining <= TimeSpan.Zero)
                return;

            var length = remaining < desiredWindow ? remaining : desiredWindow;
            if (length < TimeSpan.FromSeconds(1))
                return;

            logger.LogInformation(
                "Subtitle window label={Label} start={Start:g} length={Length:g} progress={Progress:g}",
                logLabel,
                start,
                length,
                progress);

            var audio = await audioDecoder.DecodeLocalFileAsync(
                filePath,
                start,
                length,
                cancellationToken).ConfigureAwait(false);

            if (shouldContinue is not null && !shouldContinue())
                return;

            await pipeline.SubmitAudioAsync(audio, cancellationToken).ConfigureAwait(false);
            await pipeline.PrepareTranslationsAsync(mode, audio.MediaStart, audio.MediaEnd, cancellationToken)
                .ConfigureAwait(false);

            var produced = audio.MediaEnd - audio.MediaStart;
            if (total is null && produced + TimeSpan.FromMilliseconds(500) < length)
            {
                logger.LogInformation(
                    "Subtitle sequential ASR reached EOF label={Label} covered={Covered:g}",
                    logLabel,
                    pipeline.GetSequentialCoveredUntil());
                return;
            }
        }
    }
}
