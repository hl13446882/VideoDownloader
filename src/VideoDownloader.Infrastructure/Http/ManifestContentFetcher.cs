using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Http;

public sealed class ManifestContentFetcher : IManifestContentFetcher
{
    private readonly HttpClient _client;
    private readonly IRequestMessageFactory _requestFactory;

    public ManifestContentFetcher(HttpClient client, IRequestMessageFactory requestFactory)
    {
        _client = client;
        _requestFactory = requestFactory;
    }

    public async Task<string> FetchAsync(Uri url, RequestContext context, CancellationToken ct)
    {
        var resource = new MediaResource(
            Guid.NewGuid(),
            url,
            MediaType.Unknown,
            null,
            "GET",
            null,
            null,
            null,
            null,
            url,
            null,
            context,
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        using var request = _requestFactory.Create(resource, HttpMethod.Get);
        using var response = await _client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
