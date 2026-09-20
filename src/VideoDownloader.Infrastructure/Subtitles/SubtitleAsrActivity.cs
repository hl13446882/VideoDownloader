namespace VideoDownloader.Infrastructure.Subtitles;

/// <summary>
/// Tracks whether the local-library play page holds the ASR engine.
/// Idle prewarm yields while any local player session is open (playing or paused).
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
