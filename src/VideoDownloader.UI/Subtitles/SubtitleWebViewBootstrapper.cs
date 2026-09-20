using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Subtitles;
using VideoDownloader.Infrastructure.Subtitles.Browser;

namespace VideoDownloader.UI.Subtitles;

/// <summary>
/// Attaches the local-playback subtitle runtime to WebView2. The runtime itself ignores ordinary
/// web pages and only activates for the built-in /play/{jobId} local-library player.
/// </summary>
public static class SubtitleWebViewBootstrapper
{
    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<WebView2, RuntimeHolder> Runtimes = new();
    private static readonly ConcurrentDictionary<int, WeakReference<BrowserSubtitleRuntime>> ActiveRuntimes = new();
    private static IServiceProvider? _services;
    private static bool _registered;
    private static int _nextRuntimeId;

    public static void Configure(IServiceProvider services)
    {
        _services = services;
        lock (Sync)
        {
            if (_registered)
                return;
            EventManager.RegisterClassHandler(
                typeof(WebView2),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded));
            EventManager.RegisterClassHandler(
                typeof(WebView2),
                FrameworkElement.UnloadedEvent,
                new RoutedEventHandler(OnUnloaded));
            _registered = true;
        }
    }

    public static async Task RefreshAllStylesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var pair in ActiveRuntimes.ToArray())
        {
            if (!pair.Value.TryGetTarget(out var runtime))
            {
                ActiveRuntimes.TryRemove(pair.Key, out _);
                continue;
            }

            try
            {
                await runtime.ApplyCurrentStyleAsync(cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                // Style refresh is best-effort; a disposed WebView must not block settings save.
            }
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebView2 webView)
            return;

        webView.CoreWebView2InitializationCompleted -= OnCoreInitialized;
        webView.CoreWebView2InitializationCompleted += OnCoreInitialized;
        if (webView.CoreWebView2 is not null)
            _ = AttachAsync(webView);
    }

    private static void OnCoreInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess && sender is WebView2 webView)
            _ = AttachAsync(webView);
    }

    private static async Task AttachAsync(WebView2 webView)
    {
        if (_services is null || webView.CoreWebView2 is null || Runtimes.TryGetValue(webView, out _))
            return;

        var bridge = new WebViewSubtitleBridge(
            webView,
            _services.GetRequiredService<ILoggerFactory>().CreateLogger("SubtitleBridge"));
        var runtime = new BrowserSubtitleRuntime(
            bridge,
            _services.GetRequiredService<ISubtitlePipeline>(),
            _services.GetRequiredService<IMediaAudioDecoder>(),
            _services.GetRequiredService<LocalPlaybackMediaSourceResolver>(),
            _services.GetRequiredService<SubtitleOptions>(),
            _services.GetRequiredService<SubtitleAsrActivity>(),
            _services.GetRequiredService<ILoggerFactory>().CreateLogger("BrowserSubtitle"));
        var runtimeId = Interlocked.Increment(ref _nextRuntimeId);
        var holder = new RuntimeHolder(runtime, runtimeId);
        try
        {
            Runtimes.Add(webView, holder);
            ActiveRuntimes[runtimeId] = new WeakReference<BrowserSubtitleRuntime>(runtime);
            await runtime.InitializeAsync().ConfigureAwait(true);
            var logger = _services.GetService<ILoggerFactory>()?.CreateLogger("SubtitleWebView");
            logger?.LogInformation("Subtitle runtime attached to WebView2 id={Id}", runtimeId);
        }
        catch (Exception ex)
        {
            var logger = _services.GetService<ILoggerFactory>()?.CreateLogger("SubtitleWebView");
            logger?.LogWarning(ex, "Failed to attach subtitle runtime to WebView2");
            Runtimes.Remove(webView);
            ActiveRuntimes.TryRemove(runtimeId, out _);
            await runtime.DisposeAsync();
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebView2 webView)
            return;
        webView.CoreWebView2InitializationCompleted -= OnCoreInitialized;
        if (!Runtimes.TryGetValue(webView, out var holder))
            return;
        Runtimes.Remove(webView);
        ActiveRuntimes.TryRemove(holder.RuntimeId, out _);
        _ = holder.Runtime.DisposeAsync();
    }

    private sealed class RuntimeHolder(BrowserSubtitleRuntime runtime, int runtimeId)
    {
        public BrowserSubtitleRuntime Runtime { get; } = runtime;
        public int RuntimeId { get; } = runtimeId;
    }
}
