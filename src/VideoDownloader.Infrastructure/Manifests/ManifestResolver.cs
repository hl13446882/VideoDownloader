using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Manifests;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Manifests;

public sealed class ManifestResolver : IManifestResolver
{
    private readonly IManifestContentFetcher _fetcher;

    public ManifestResolver(IManifestContentFetcher fetcher)
    {
        _fetcher = fetcher;
    }

    public async Task<ManifestResolutionResult> ResolveHlsAsync(
        MediaResource manifest,
        CancellationToken ct)
    {
        var content = await _fetcher.FetchAsync(manifest.Url, manifest.RequestContext, ct);
        var parsed = HlsManifestParser.Parse(content, manifest.Url, manifest.RequestContext);
        return new ManifestResolutionResult(parsed.Variants, parsed.IsDrmProtected);
    }

    public async Task<ManifestResolutionResult> ResolveDashAsync(
        MediaResource manifest,
        CancellationToken ct)
    {
        var content = await _fetcher.FetchAsync(manifest.Url, manifest.RequestContext, ct);
        var parsed = DashManifestParser.Parse(content, manifest.Url, manifest.RequestContext);
        return new ManifestResolutionResult(parsed.Variants, parsed.IsDrmProtected);
    }
}
