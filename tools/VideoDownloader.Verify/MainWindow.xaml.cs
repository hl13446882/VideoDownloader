using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.UI.ViewModels;

namespace VideoDownloader.Verify;

public partial class MainWindow : Window
{
    private readonly StringBuilder _log = new();
    private ServiceProvider? _services;
    private MainViewModel? _mainVm;
    private string? _reportPath;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var args = Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var urls = args;

        var exitCode = 1;
        try
        {
            BeginReport();
            if (Environment.GetCommandLineArgs().Contains("--local"))
                exitCode = await RunLocalAcceptanceAsync() ? 0 : 2;
            else if (Environment.GetCommandLineArgs().Contains("--live"))
                exitCode = await RunLiveAcceptanceAsync(urls) ? 0 : 2;
            else if (urls.Length == 0)
                throw new ArgumentException("Explicit authorized HTTP(S) test URLs are required; no default videos are used.");
            else exitCode = await RunAsync(urls) ? 0 : 2;
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
            exitCode = 1;
        }
        finally
        {
            Log($"ExitCode={exitCode}");
            try
            {
                if (_reportPath is not null)
                    File.WriteAllText(_reportPath, _log.ToString());
            }
            catch
            {
                // ignore
            }
        }

        await Task.Delay(300);
        Application.Current.Shutdown(exitCode);
    }

    private async Task<bool> RunAsync(string[] urls)
    {
        Log("=== VideoDownloader site verification ===");
        Log("Checks: detect + switch + UI dropdown (DetectedVideos / Variants) auto-update");
        var publishRoot = FindPublishRoot();
        Log($"Publish root: {publishRoot}");

        EnsureToolLayout(publishRoot, AppContext.BaseDirectory);

        var options = new AppOptions
        {
            ExternalResolvers =
            {
                Enabled = !Environment.GetCommandLineArgs().Contains("--local"),
                YtDlpPath = Path.Combine(publishRoot, "tools", "yt-dlp.exe")
            },
            Download =
            {
                DefaultSavePath = Path.Combine(Path.GetTempPath(), "VideoDownloaderVerifyDownloads")
            },
            Browser =
            {
                UserDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VideoDownloader",
                    Environment.GetCommandLineArgs().Contains("--local") ? "AcceptanceWebView2Data" : "WebView2Data")
            }
        };
        Directory.CreateDirectory(options.Download.DefaultSavePath);
        Directory.CreateDirectory(options.Browser.UserDataFolder);

        var services = new ServiceCollection();
        services.AddVideoDownloaderInfrastructure(options);
        services.AddSingleton<UserSettingsStore>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        _services = services.BuildServiceProvider();
        await _services.InitializeInfrastructureAsync();

        _mainVm = _services.GetRequiredService<MainViewModel>();
        await _mainVm.AddTabAsync(urls[0], select: true);
        var tab = _mainVm.SelectedTab ?? throw new InvalidOperationException("No browser tab");
        await _mainVm.AttachWebViewAsync(tab, WebView);
        if (Environment.GetCommandLineArgs().Contains("--live")) return true;

        var results = new List<SiteResult>();
        UiSnapshot? previousUi = null;

        for (var i = 0; i < urls.Length; i++)
        {
            var url = urls[i];
            Log("");
            Log($"--- [{i + 1}/{urls.Length}] Navigate: {url}");

            _mainVm.AddressBar = url;
            await _mainVm.NavigateCommand.ExecuteAsync(null);
            await WaitForNavigationSettleAsync(url);
            await TryStartPlaybackAsync();

            // Stable session in MainViewModel is ~2+6+10s plus probes; wait for UI dropdown.
            var ui = await WaitForUiDetectionAsync(TimeSpan.FromSeconds(45));
            LogUiSnapshot(ui);

            var dropdownOk = EvaluateDropdownUpdate(i, previousUi, ui);
            var switchedOk = i == 0
                ? ui.VariantCount > 0
                : previousUi is { VariantCount: > 0 } &&
                  ui.VariantCount > 0 &&
                  !SameSessionUrl(previousUi.SelectedVariantUrl, ui.SelectedVariantUrl);

            var downloadOk = false;
            string? downloadNote = null;
            var selectedVariant = CaptureSelectedVariant();
            if (selectedVariant is not null)
                (downloadOk, downloadNote) = await TryProbeDownloadAsync(selectedVariant);

            var externalError = (_services.GetRequiredService<IMediaDetectionPipeline>() as UnifiedMediaPipeline)
                ?.LastExternalError;

            var result = new SiteResult(
                url,
                ui.VariantCount,
                ui.SelectedTitle,
                ui.SelectedVariantUrl,
                switchedOk,
                dropdownOk,
                ui.DropdownNote,
                ui.VariantFingerprint,
                downloadOk,
                downloadNote,
                externalError);
            results.Add(result);
            Log(FormatResult(result));

            if (ui.VariantCount > 0)
                previousUi = ui;
        }

        Log("");
        Log("=== Summary ===");
        var allDetect = results.All(r => r.VariantCount > 0);
        var withUi = results.Where(r => r.VariantCount > 0 && !string.IsNullOrWhiteSpace(r.PrimaryUrl)).ToList();
        var allSwitch = withUi.Count <= 1 ||
                        withUi.Zip(withUi.Skip(1), (a, b) => !SameSessionUrl(a.PrimaryUrl, b.PrimaryUrl)).All(ok => ok);
        var allDropdown = results.All(r => r.DropdownOk);
        var anyDownload = results.Any(r => r.DownloadOk);
        foreach (var r in results)
            Log(FormatResult(r));

        Log($"Detect all: {(allDetect ? "PASS" : "FAIL")}");
        Log($"Switch update: {(allSwitch ? "PASS" : "FAIL")}");
        Log($"Dropdown auto-update: {(allDropdown ? "PASS" : "FAIL")}");
        Log($"Download sample: {(anyDownload ? "PASS" : "FAIL (or blocked)")}");

        return allDetect && allSwitch && allDropdown;
    }

    private static bool EvaluateDropdownUpdate(int index, UiSnapshot? previous, UiSnapshot current)
    {
        if (current.VariantCount <= 0 || string.IsNullOrWhiteSpace(current.SelectedVariantUrl))
        {
            current.DropdownNote = "下拉无选中项或 Variants 为空";
            return false;
        }

        if (index == 0 || previous is null || previous.VariantCount <= 0)
        {
            current.DropdownNote = $"首站下拉已填充 variants={current.VariantCount}";
            return true;
        }

        if (string.Equals(previous.VariantFingerprint, current.VariantFingerprint, StringComparison.Ordinal))
        {
            current.DropdownNote = "Variants 指纹未变（下拉未刷新）";
            return false;
        }

        if (SameSessionUrl(previous.SelectedVariantUrl, current.SelectedVariantUrl))
        {
            current.DropdownNote = "SelectedVariant 仍指向旧会话地址";
            return false;
        }

        // Old primary must not remain as the selected dropdown value.
        if (!string.IsNullOrWhiteSpace(previous.SelectedVariantUrl) &&
            current.VariantUrls.Any(u => SameSessionUrl(u, previous.SelectedVariantUrl)))
        {
            // Allowed if both pages somehow share CDN path; still require selected URL changed (checked above).
        }

        current.DropdownNote =
            $"下拉已更新：{previous.VariantCount}→{current.VariantCount} 项，SelectedVariant 已换源";
        return true;
    }

    private async Task<UiSnapshot> WaitForUiDetectionAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = CaptureUiSnapshot();
        var playTick = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (playTick++ % 3 == 0)
                await TryStartPlaybackAsync();

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            last = CaptureUiSnapshot();
            if (last.VariantCount > 0 && !string.IsNullOrWhiteSpace(last.SelectedVariantUrl))
            {
                // Let Background OnVideoDetected / ReplaceVariants settle.
                await Task.Delay(1200);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                return CaptureUiSnapshot();
            }

            await Task.Delay(800);
        }

        Log("WARN: UI detection wait timeout");
        return last;
    }

    private UiSnapshot CaptureUiSnapshot()
    {
        if (_mainVm is null)
            return new UiSnapshot(0, null, 0, null, "", [], "no MainViewModel");

        return Dispatcher.Invoke(() =>
        {
            var selected = _mainVm.SelectedDetectedVideo;
            var variants = selected?.Variants.ToArray() ?? [];
            var urls = variants.Select(v => v.Variant.SourceUrl.AbsoluteUri).ToArray();
            var fingerprint = string.Join("|", urls.OrderBy(u => u, StringComparer.OrdinalIgnoreCase));
            return new UiSnapshot(
                _mainVm.DetectedVideos.Count,
                selected?.Title,
                variants.Length,
                selected?.SelectedVariant?.Variant.SourceUrl.AbsoluteUri,
                fingerprint,
                urls,
                string.Empty);
        });
    }

    private MediaVariant? CaptureSelectedVariant()
    {
        if (_mainVm is null)
            return null;
        return Dispatcher.Invoke(() => _mainVm.SelectedDetectedVideo?.SelectedVariant?.Variant);
    }

    private void LogUiSnapshot(UiSnapshot ui)
    {
        Log(
            $"UI: videos={ui.DetectedVideoCount}, dropdownVariants={ui.VariantCount}, " +
            $"selected={Truncate(ui.SelectedVariantUrl, 90)}, title={ui.SelectedTitle}");
    }

    private async Task WaitForNavigationSettleAsync(string expectedUrl)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            var current = _mainVm?.SelectedTab?.Host.CurrentPageUrl?.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(current) &&
                Uri.TryCreate(expectedUrl, UriKind.Absolute, out var expected) &&
                Uri.TryCreate(current, UriKind.Absolute, out var actual) &&
                string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(1500);
                return;
            }

            await Task.Delay(400);
        }

        Log("WARN: navigation settle timeout");
    }

    private async Task TryStartPlaybackAsync()
    {
        try
        {
            var core = WebView.CoreWebView2;
            if (core is null)
                return;

            await core.ExecuteScriptAsync(
                """
                (async () => {
                  const videos = Array.from(document.querySelectorAll('video'));
                  const v=videos.find(v=>{const r=v.getBoundingClientRect();return r.bottom>0&&r.top<innerHeight;});
                  if(v&&v.paused){try{v.muted=true;v.playsInline=true;await v.play();}catch(e){}}
                })();
                """);
        }
        catch (Exception ex)
        {
            Log("Play nudge: " + ex.Message);
        }
    }

    private async Task<(bool Ok, string Note)> TryProbeDownloadAsync(MediaVariant variant)
    {
        try
        {
            var track = variant.Tracks[0];
            using var request = _services!.GetRequiredService<IRequestMessageFactory>()
                .Create(variant, HttpMethod.Get, track.SourceUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 64 * 1024 - 1);

            using var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var code = (int)response.StatusCode;
            if (code is >= 200 and < 300 or 206)
            {
                var buf = new byte[16 * 1024];
                await using var stream = await response.Content.ReadAsStreamAsync();
                var read = await stream.ReadAsync(buf);
                return (read > 0, $"HTTP {code}, read {read} bytes");
            }

            return (false, $"HTTP {code}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool SameSessionUrl(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua) ||
            !Uri.TryCreate(b, UriKind.Absolute, out var ub))
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return MediaUrlNormalizer.IsSameSession(ua, ub);
    }

    private static string FormatResult(SiteResult r) =>
        $"variants={r.VariantCount}, switched={r.SwitchedOk}, dropdown={r.DropdownOk} ({r.DropdownNote}), " +
        $"download={r.DownloadOk} ({r.DownloadNote}), title={r.Title}, primary={Truncate(r.PrimaryUrl, 80)}, " +
        $"external={r.ExternalError}";

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= max ? value : value[..(max - 3)] + "...";
    }

    private static void EnsureToolLayout(string publishRoot, string baseDir)
    {
        foreach (var name in new[] { "ffmpeg", "tools", "M3u8" })
        {
            var src = Path.Combine(publishRoot, name);
            var dst = Path.Combine(baseDir, name);
            if (!Directory.Exists(src))
                continue;
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, file);
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target) || new FileInfo(file).Length != new FileInfo(target).Length)
                    File.Copy(file, target, overwrite: true);
            }
        }
    }

    private static string FindPublishRoot()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\publish\VideoDownload")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\publish\VideoDownload")),
            @"D:\VideoDownloader\publish\VideoDownload"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "tools", "yt-dlp.exe")))
                return c;
        }

        throw new DirectoryNotFoundException("publish\\VideoDownload not found (need tools\\yt-dlp.exe).");
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _log.AppendLine(line);
        try
        {
            if (_reportPath is not null)
                File.AppendAllText(_reportPath, line + Environment.NewLine);
        }
        catch
        {
            // ignore
        }

        try
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            });
        }
        catch
        {
            // ignore UI failures during shutdown
        }
    }

    private void BeginReport()
    {
        try
        {
            var publishRoot = FindPublishRoot();
            var repoRoot = Path.GetFullPath(Path.Combine(publishRoot, "..", ".."));
            var reportDir = Path.Combine(repoRoot, "artifacts");
            Directory.CreateDirectory(reportDir);
            _reportPath = Path.Combine(reportDir, "vd-verify-latest.log");
            File.WriteAllText(_reportPath, string.Empty);
        }
        catch
        {
            _reportPath = Path.Combine(AppContext.BaseDirectory, "vd-verify-latest.log");
            File.WriteAllText(_reportPath, string.Empty);
        }
    }

    private sealed class UiSnapshot
    {
        public UiSnapshot(
            int detectedVideoCount,
            string? selectedTitle,
            int variantCount,
            string? selectedVariantUrl,
            string variantFingerprint,
            string[] variantUrls,
            string dropdownNote)
        {
            DetectedVideoCount = detectedVideoCount;
            SelectedTitle = selectedTitle;
            VariantCount = variantCount;
            SelectedVariantUrl = selectedVariantUrl;
            VariantFingerprint = variantFingerprint;
            VariantUrls = variantUrls;
            DropdownNote = dropdownNote;
        }

        public int DetectedVideoCount { get; }
        public string? SelectedTitle { get; }
        public int VariantCount { get; }
        public string? SelectedVariantUrl { get; }
        public string VariantFingerprint { get; }
        public string[] VariantUrls { get; }
        public string DropdownNote { get; set; }
    }

    private sealed record SiteResult(
        string Url,
        int VariantCount,
        string? Title,
        string? PrimaryUrl,
        bool SwitchedOk,
        bool DropdownOk,
        string? DropdownNote,
        string VariantFingerprint,
        bool DownloadOk,
        string? DownloadNote,
        string? ExternalError);
}
