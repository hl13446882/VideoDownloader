using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection.Sites.YouTube;

namespace VideoDownloader.Infrastructure.Tests;

internal sealed class GatedYouTubeExtractor : YouTubeYtDlpExtractor
{
    private readonly TaskCompletionSource<IReadOnlyList<DetectedVideo>> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _available;

    public GatedYouTubeExtractor(bool available = true, bool completesImmediately = false)
        : base(
            Options.Create(new AppOptions
            {
                ExternalResolvers = new ExternalResolversOptions { Enabled = false }
            }),
            NullLogger<YouTubeYtDlpExtractor>.Instance,
            Substitute.For<IProbeMethodStats>())
    {
        _available = available;
        if (completesImmediately)
            _result.TrySetResult([]);
    }

    public override bool IsAvailable => _available;

    public override Task<IReadOnlyList<DetectedVideo>> ResolveAsync(
        Uri pageUrl, RequestContext context, CancellationToken ct) =>
        _result.Task.WaitAsync(ct);

    public void Complete(params DetectedVideo[] videos) => _result.TrySetResult(videos);
}
