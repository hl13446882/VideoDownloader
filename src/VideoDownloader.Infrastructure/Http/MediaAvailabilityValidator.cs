using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Http;

public sealed class MediaAvailabilityValidator(IHttpClientFactory clients, IRequestMessageFactory requests, ILogger<MediaAvailabilityValidator> logger)
{
    public async Task ValidateAsync(MediaVariant variant, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        foreach (var track in variant.Tracks.DistinctBy(t => t.SourceUrl))
        {
            // Manifest backends validate initialization/segments; binary sniffing is for direct resources only.
            if (track.Container is "hls" or "dash") continue;
            try { await ReadMediaAsync(track, track.SourceUrl, 0, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new DownloadException(ErrorCodes.NetTimeout, "Media sample validation timed out."); }
        }
    }

    private async Task ReadMediaAsync(MediaTrack track, Uri source, int depth, CancellationToken ct)
    {
        if (depth > 3) throw new DownloadException(ErrorCodes.InvalidFormat, "Playlist nesting limit exceeded.");
        var url = source;
        using var client = clients.CreateClient("media-primary");
        for (var redirect = 0; redirect < 8; redirect++)
        {
            if (url.Scheme is not ("http" or "https")) throw new DownloadException(ErrorCodes.InvalidFormat, "Unsupported media protocol.");
            using var request = requests.Create(MediaVariant.FromTracks("sample", null, null, null, track.Container, [track]), HttpMethod.Get, url);
            request.Headers.Remove("Range");
            request.Headers.Remove("If-Range");
            request.Headers.Range = new RangeHeaderValue(0, 65535);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            logger.LogInformation("Media validation host={Host} status={Status} depth={Depth}", url.Host, (int)response.StatusCode, depth);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new DownloadException(ErrorCodes.InvalidFormat, "Redirect missing Location.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new DownloadException(ErrorCodes.Http403, "Media sample denied (403).");
            if (!response.IsSuccessStatusCode) throw new DownloadException(response.StatusCode == HttpStatusCode.NotFound ? ErrorCodes.Http404 : ErrorCodes.InvalidFormat, $"Media sample HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var data = new byte[65536];
            var count = 0;
            while (count < data.Length)
            {
                var read = await stream.ReadAsync(data.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count == 0) throw new DownloadException(ErrorCodes.InvalidFormat, "Empty media sample.");
            var mime = response.Content.Headers.ContentType?.MediaType ?? "";
            var text = Encoding.UTF8.GetString(data, 0, count).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (text.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                var child = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
                if (child is null) throw new DownloadException(ErrorCodes.InvalidFormat, "Playlist contains no sample resource.");
                await ReadMediaAsync(track, new Uri(url, child), depth + 1, ct);
                return;
            }
            if (text.StartsWith('<') || text.StartsWith('{') || text.StartsWith('[') || mime.Contains("html") || mime.Contains("json"))
                throw new DownloadException(ErrorCodes.InvalidFormat, "Response is a document, not media bytes.");
            if (!LooksLikeMedia(data.AsSpan(0, count)))
                throw new DownloadException(ErrorCodes.InvalidFormat, "Media sample has no recognized container header.");
            return;
        }
        throw new DownloadException(ErrorCodes.NetTimeout, "Media redirect limit exceeded.");
    }

    internal static bool LooksLikeMedia(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && (b.Slice(4, 4).SequenceEqual("ftyp"u8) || b.Slice(4, 4).SequenceEqual("styp"u8) ||
            b.Slice(4, 4).SequenceEqual("moof"u8) || b.Slice(4, 4).SequenceEqual("moov"u8) ||
            b.StartsWith("ID3"u8) || b.StartsWith("OggS"u8) || b.StartsWith("RIFF"u8) || b.StartsWith("fLaC"u8) ||
            b.StartsWith("FLV"u8) || b.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }) ||
            (b[0] == 0x47 && b.Length > 188 && b[188] == 0x47) || (b[0] == 0xff && (b[1] & 0xe0) == 0xe0));
}
