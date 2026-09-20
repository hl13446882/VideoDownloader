namespace VideoDownloader.Infrastructure.Subtitles;

/// <summary>
/// Tracks whether any local-player subtitle session is active so idle prewarm can yield the ASR engine.
/// </summary>
public sealed class SubtitleAsrActivity
{
    private int _playbackSessions;

    public bool IsPlaybackActive => Volatile.Read(ref _playbackSessions) > 0;

    public event EventHandler? Changed;

    public void EnterPlayback()
    {
        if (Interlocked.Increment(ref _playbackSessions) == 1)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void LeavePlayback()
    {
        while (true)
        {
            var current = Volatile.Read(ref _playbackSessions);
            if (current <= 0)
                return;
            if (Interlocked.CompareExchange(ref _playbackSessions, current - 1, current) == current)
            {
                if (current == 1)
                    Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
    }
}
