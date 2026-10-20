namespace VideoDownloader.Infrastructure.Browser;

internal static class YouTubeLivePlaybackScript
{
    internal const string Definition = """
        const __vdYouTubeIsLive = () => {
          if (!/(^|\.)(youtube\.com|youtube-nocookie\.com|youtu\.be)$/i.test(location.hostname)) return false;
          const id = new URLSearchParams(location.search).get('v')
            || (location.pathname.match(/\/(?:live|shorts)\/([^/?#]+)/)||[])[1];
          const player = document.getElementById('movie_player');
          let response, data;
          try { response = player?.getPlayerResponse?.(); data = player?.getVideoData?.(); } catch {}
          if (!response?.videoDetails || (id && response.videoDetails.videoId !== id))
            response = window.ytInitialPlayerResponse;
          if (id && response?.videoDetails?.videoId !== id) response = null;
          if (id && data?.video_id !== id) data = null;
          const broadcast = response?.microformat?.playerMicroformatRenderer?.liveBroadcastDetails;
          // Ended broadcasts retain isLiveContent. They are downloadable recordings.
          if (broadcast?.endTimestamp) return false;
          if (broadcast?.isLiveNow === true || data?.isLive === true || response?.videoDetails?.isLive === true)
            return true;
          if (response?.videoDetails?.isLiveContent === true && broadcast?.startTimestamp) return true;
          return [...document.querySelectorAll('video')].some(v => v.duration === Infinity);
        };
        """;
}
