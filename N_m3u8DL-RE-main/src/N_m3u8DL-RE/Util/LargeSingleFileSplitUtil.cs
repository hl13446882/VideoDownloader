using N_m3u8DL_RE.Common.Entity;
using System.Net;
using N_m3u8DL_RE.Common.Util;

namespace N_m3u8DL_RE.Util;

internal static class LargeSingleFileSplitUtil
{
    public static async Task<List<MediaSegment>?> SplitUrlAsync(MediaSegment segment, Dictionary<string, string> headers)
    {
        if (segment.StartRange != null) return null;
        var size = await ProbeRangeAsync(segment.Url, headers);
        if (size is null || size <= 8 * 1024 * 1024) return null;
        const long chunkSize = 8 * 1024 * 1024;
        var segments = new List<MediaSegment>();
        for (long start = 0; start < size; start += chunkSize)
            segments.Add(new MediaSegment { Index = segments.Count, Url = segment.Url,
                StartRange = start, ExpectLength = Math.Min(chunkSize, size.Value - start), EncryptInfo = segment.EncryptInfo });
        return segments;
    }

    public static async Task<bool> CanSplitAsync(string url, Dictionary<string, string> headers) =>
        await ProbeRangeAsync(url, headers) is > 0;

    private static async Task<long?> ProbeRangeAsync(string url, Dictionary<string, string> headers)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (var redirects = 0; redirects < 8; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var h in headers.Where(h => !h.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)))
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                request.Headers.Range = new(0, 0);
                using var response = await HTTPUtil.AppHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    url = new Uri(new Uri(url), location).AbsoluteUri;
                    continue;
                }
                var range = response.Content.Headers.ContentRange;
                return response.StatusCode == HttpStatusCode.PartialContent && range?.From == 0 && range.To == 0 ? range.Length : null;
            }
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) { }
        return null;
    }
}
