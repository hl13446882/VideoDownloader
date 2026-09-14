using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using System.Net;
using System.Net.Sockets;
using VideoDownloader.Core.Aggregation;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Sites;
using VideoDownloader.Infrastructure.Browser;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Ffmpeg;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Logging;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.Manifests;
using VideoDownloader.Infrastructure.Persistence;
using VideoDownloader.Infrastructure.Sites;
using VideoDownloader.Infrastructure.Sites.ExternalResolvers;
using VideoDownloader.Infrastructure.Update;

namespace VideoDownloader.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVideoDownloaderInfrastructure(
        this IServiceCollection services,
        AppOptions? options = null)
    {
        options ??= new AppOptions();
        services.AddSingleton(options);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        var appLog = new AppLogService(options);
        appLog.ApplyFromOptions();
        services.AddSingleton(appLog);

        services.AddSingleton<INetworkEventNormalizer>(sp =>
            new NetworkEventNormalizer(
                options.Detection.DedupCacheMaxEntries,
                TimeSpan.FromMinutes(options.Detection.DedupCacheTtlMinutes)));

        services.AddSingleton<IMediaDetector, MediaDetector>();
        services.AddSingleton<IMediaAggregator, MediaAggregator>();
        services.AddSingleton<IManifestResolver, ManifestResolver>();
        // Physically isolated yt-dlp extractors (one instance per detector family).
        // Douyin has no yt-dlp extractor — network/observation only.
        services.AddSingleton<VideoDownloader.Infrastructure.Detection.Sites.YouTube.YouTubeYtDlpExtractor>();
        services.AddSingleton<VideoDownloader.Infrastructure.Detection.Sites.TikTok.TikTokYtDlpExtractor>();
        services.AddSingleton<VideoDownloader.Infrastructure.Detection.Sites.Bilibili.BilibiliYtDlpExtractor>();
        services.AddSingleton<VideoDownloader.Infrastructure.Detection.Sites.Generic.GenericYtDlpExtractor>();
        // Download renewal may enumerate by SupportsSite; do not register the retired shared YtDlpResolver.
        services.AddSingleton<IExternalSiteResolver>(sp => sp.GetRequiredService<VideoDownloader.Infrastructure.Detection.Sites.YouTube.YouTubeYtDlpExtractor>());
        services.AddSingleton<IExternalSiteResolver>(sp => sp.GetRequiredService<VideoDownloader.Infrastructure.Detection.Sites.TikTok.TikTokYtDlpExtractor>());
        services.AddSingleton<IExternalSiteResolver>(sp => sp.GetRequiredService<VideoDownloader.Infrastructure.Detection.Sites.Bilibili.BilibiliYtDlpExtractor>());
        services.AddSingleton<IExternalSiteResolver>(sp => sp.GetRequiredService<VideoDownloader.Infrastructure.Detection.Sites.Generic.GenericYtDlpExtractor>());
        services.AddSingleton<ISiteMediaAdapter, VideoDownloader.Infrastructure.Sites.MediaAdapters.GenericMediaAdapter>();
        // Douyin/TikTok/YouTube/Bilibili use exclusive detectors — do not register MediaAdapters.
        services.AddSingleton<IProbeMethodStats, VideoDownloader.Infrastructure.Detection.ProbeMethodStatsStore>();
        services.AddSingleton<ICandidateDecisionPolicy, CandidateDecisionPolicy>();
        services.AddSingleton<ISiteMediaAdapterResolver, SiteMediaAdapterResolver>();
        services.AddSingleton<ISiteDetectionRouter, SiteDetectionRouter>();
        services.AddSingleton<VideoDownloader.Core.Contracts.IExclusiveSiteMediaDetector, VideoDownloader.Infrastructure.Detection.Sites.Douyin.DouyinMediaDetector>();
        services.AddSingleton<VideoDownloader.Core.Contracts.IExclusiveSiteMediaDetector, VideoDownloader.Infrastructure.Detection.Sites.TikTok.TikTokMediaDetector>();
        services.AddSingleton<VideoDownloader.Core.Contracts.IExclusiveSiteMediaDetector, VideoDownloader.Infrastructure.Detection.Sites.YouTube.YouTubeMediaDetector>();
        services.AddSingleton<VideoDownloader.Core.Contracts.IExclusiveSiteMediaDetector, VideoDownloader.Infrastructure.Detection.Sites.Bilibili.BilibiliMediaDetector>();
        services.AddSingleton<IExclusiveSiteMediaDetectorResolver, ExclusiveSiteMediaDetectorResolver>();
        services.AddSingleton<VideoDownloader.Infrastructure.Detection.UnifiedMediaPipeline>();
        services.AddSingleton<IMediaDetectionPipeline, VideoDownloader.Infrastructure.Detection.RoutedMediaDetectionPipeline>();
        services.AddSingleton<VideoDownloader.Infrastructure.Download.M3u8DownloadAdapter>();
        services.AddSingleton<IRequestMessageFactory, RequestMessageFactory>();
        services.AddSingleton<VideoDownloader.Infrastructure.Http.MediaAvailabilityValidator>();
        services.AddSingleton<IDownloadJobStateMachine, DownloadJobStateMachine>();
        services.AddSingleton<IDownloadRepository, SqliteDownloadRepository>();
        services.AddSingleton<ILibraryKindCatalog, SqliteLibraryKindCatalog>();
        services.AddSingleton<IDownloadBackendRouter, DownloadBackendRouter>();
        services.AddSingleton<IDownloadEngine, DownloadEngine>();
        services.AddSingleton<IMediaAddressRediscoverer, BrowserMediaAddressRediscoverer>();
        services.AddSingleton<IFfmpegAdapter, FfmpegAdapter>();
        services.AddSingleton<VideoDownloader.Infrastructure.LocalLibrary.LocalVideoThumbnailStore>();
        services.AddSingleton<VideoDownloader.Infrastructure.LocalLibrary.LocalLibraryHost>();
        services.AddHttpClient("license", client =>
        {
            client.BaseAddress = new Uri(options.License.Endpoint.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(8);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        services.AddSingleton(sp => new LicenseService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("license"),
            sp.GetRequiredService<IOptions<AppOptions>>(),
            sp.GetRequiredService<ILogger<LicenseService>>()));

        services.AddHttpClient("update", client =>
        {
            client.BaseAddress = new Uri(options.Update.Endpoint.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMinutes(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        services.AddSingleton(sp => new UpdateService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("update"),
            sp.GetRequiredService<LicenseService>(),
            sp.GetRequiredService<IOptions<AppOptions>>(),
            sp.GetRequiredService<ILogger<UpdateService>>()));

        services.AddHttpClient("media-primary", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(30);
            })
            .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);

        // Keep the system proxy and route as the primary path. This client is only used
        // after a TCP connection failure, so an unusable IPv6/CDN route can self-heal.
        services.AddHttpClient("ipv4-direct-fallback", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(30);
            })
            .ConfigurePrimaryHttpMessageHandler(CreateIpv4DirectHttpHandler);

        services.AddTransient(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new HttpMediaDownloader(
                factory.CreateClient("media-primary"),
                factory.CreateClient("ipv4-direct-fallback"),
                sp.GetRequiredService<IRequestMessageFactory>(),
                sp.GetRequiredService<IOptions<AppOptions>>(),
                sp.GetRequiredService<ILogger<HttpMediaDownloader>>(),
                sp.GetRequiredService<LicenseService>());
        });

        services.AddHttpClient<IManifestContentFetcher, ManifestContentFetcher>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);

        services.AddSingleton<BrowserHostLocator>();
        services.AddTransient<WebView2Host>();
        services.AddSingleton<IRequestContextProvider, RequestContextProvider>();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        return services;
    }

    private static SocketsHttpHandler CreateHttpHandler() => new()
    {
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 32
    };

    private static SocketsHttpHandler CreateIpv4DirectHttpHandler() => new()
    {
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 32,
        ConnectCallback = ConnectIpv4Async
    };

    private static async ValueTask<Stream> ConnectIpv4Async(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        SocketException? lastError = null;

        foreach (var address in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), connectTimeout.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                lastError = new SocketException((int)SocketError.TimedOut);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    public static async Task InitializeInfrastructureAsync(this IServiceProvider services)
    {
        var repo = services.GetRequiredService<IDownloadRepository>();
        await repo.InitializeAsync();

        await services.GetRequiredService<ILibraryKindCatalog>().InitializeAsync();

        await services.GetRequiredService<LicenseService>().InitializeAsync();

        var engine = services.GetRequiredService<IDownloadEngine>();
        await engine.RecoverOnStartupAsync();

        try
        {
            await services.GetRequiredService<VideoDownloader.Infrastructure.LocalLibrary.LocalLibraryHost>()
                .StartAsync();
        }
        catch (Exception)
        {
            // Gallery is optional; downloads still work if the port cannot bind.
        }
    }
}
