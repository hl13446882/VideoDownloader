using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Subtitles.Browser;

namespace VideoDownloader.UI.Subtitles;

/// <summary>
/// Attaches one isolated subtitle runtime to each WebView2 after the existing browser host
/// has initialized CoreWebView2. No changes to the media-detection pipeline are required.
/// </summary>
public static class SubtitleWebViewBootstrapper
{
    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<WebView2, RuntimeHolder> Runtimes = new();
    private static IServiceProvider? _services;
    private static bool _registered;

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

        var bridge = new WebViewSubtitleBridge(webView);
        var runtime = new BrowserSubtitleRuntime(
            bridge,
            _services.GetRequiredService<ISubtitlePipeline>(),
            _services.GetRequiredService<IMediaAudioDecoder>());
        var holder = new RuntimeHolder(runtime);
        try
        {
            Runtimes.Add(webView, holder);
            await runtime.InitializeAsync().ConfigureAwait(true);
        }
        catch
        {
            Runtimes.Remove(webView);
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
        _ = holder.Runtime.DisposeAsync();
    }

    private sealed class RuntimeHolder(BrowserSubtitleRuntime runtime)
    {
        public BrowserSubtitleRuntime Runtime { get; } = runtime;
    }
}
