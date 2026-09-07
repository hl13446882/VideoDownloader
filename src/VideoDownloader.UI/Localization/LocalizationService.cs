using CommunityToolkit.Mvvm.ComponentModel;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.UI.Localization;

public sealed partial class LocalizationService : ObservableObject
{
    private readonly AppOptions _options;
    private readonly UserSettingsStore _store;

    public LocalizationService(AppOptions options, UserSettingsStore store)
    {
        _options = options;
        _store = store;
        _isChinese = !string.Equals(options.Ui.Language, "en", StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    private bool _isChinese;

    public bool IsEnglish => !IsChinese;
    public string LanguageCode => IsChinese ? "zh-CN" : "en";
    public string LanguageToggleLabel => IsChinese ? "EN" : "中";
    public string LanguageToggleTip => T("tip.language");

    public event EventHandler? LanguageChanged;

    public void ToggleLanguage()
    {
        IsChinese = !IsChinese;
        _options.Ui.Language = LanguageCode;
        _store.Save(_options);
        RefreshBindings();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyFromOptions()
    {
        IsChinese = !string.Equals(_options.Ui.Language, "en", StringComparison.OrdinalIgnoreCase);
        RefreshBindings();
    }

    public string T(string key) =>
        Table.TryGetValue(key, out var pair)
            ? (IsChinese ? pair.Zh : pair.En)
            : key;

    public string Format(string key, params object[] args)
    {
        var template = T(key);
        try { return string.Format(template, args); }
        catch { return template; }
    }

    private void RefreshBindings()
    {
        OnPropertyChanged(nameof(IsChinese));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(LanguageCode));
        OnPropertyChanged(nameof(LanguageToggleLabel));
        OnPropertyChanged(nameof(LanguageToggleTip));
        foreach (var key in Table.Keys)
            OnPropertyChanged(key);
        // Convenience aliases used by XAML
        OnPropertyChanged(nameof(NavBack));
        OnPropertyChanged(nameof(NavForward));
        OnPropertyChanged(nameof(NavGo));
        OnPropertyChanged(nameof(NavProbe));
        OnPropertyChanged(nameof(NavNewTab));
        OnPropertyChanged(nameof(NavSettings));
        OnPropertyChanged(nameof(TipBack));
        OnPropertyChanged(nameof(TipForward));
        OnPropertyChanged(nameof(TipGo));
        OnPropertyChanged(nameof(TipProbe));
        OnPropertyChanged(nameof(TipNewTab));
        OnPropertyChanged(nameof(TipSettings));
        OnPropertyChanged(nameof(SideDetected));
        OnPropertyChanged(nameof(SideQueue));
        OnPropertyChanged(nameof(ModeVideo));
        OnPropertyChanged(nameof(ModeAudio));
        OnPropertyChanged(nameof(BtnDownload));
        OnPropertyChanged(nameof(BtnPause));
        OnPropertyChanged(nameof(BtnResume));
        OnPropertyChanged(nameof(BtnCancel));
        OnPropertyChanged(nameof(BtnRemove));
        OnPropertyChanged(nameof(MenuPlay));
        OnPropertyChanged(nameof(MenuRename));
        OnPropertyChanged(nameof(MenuOpenUrl));
        OnPropertyChanged(nameof(MenuPause));
        OnPropertyChanged(nameof(MenuResume));
        OnPropertyChanged(nameof(MenuCancel));
        OnPropertyChanged(nameof(MenuRemove));
        OnPropertyChanged(nameof(SettingsTitle));
        OnPropertyChanged(nameof(SettingsSavePath));
        OnPropertyChanged(nameof(SettingsMaxConcurrent));
        OnPropertyChanged(nameof(SettingsRetry));
        OnPropertyChanged(nameof(SettingsLogLevel));
        OnPropertyChanged(nameof(SettingsAutoRecover));
        OnPropertyChanged(nameof(SettingsSave));
        OnPropertyChanged(nameof(SettingsClose));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(QueueTooltip));
    }

    public string NavBack => T("nav.back");
    public string NavForward => T("nav.forward");
    public string NavGo => T("nav.go");
    public string NavProbe => T("nav.probe");
    public string NavNewTab => T("nav.newTab");
    public string NavSettings => T("nav.settings");
    public string TipBack => T("tip.back");
    public string TipForward => T("tip.forward");
    public string TipGo => T("tip.go");
    public string TipProbe => T("tip.probe");
    public string TipNewTab => T("tip.newTab");
    public string TipSettings => T("tip.settings");
    public string SideDetected => T("side.detected");
    public string SideQueue => T("side.queue");
    public string ModeVideo => T("mode.video");
    public string ModeAudio => T("mode.audio");
    public string BtnDownload => T("btn.download");
    public string BtnPause => T("btn.pause");
    public string BtnResume => T("btn.resume");
    public string BtnCancel => T("btn.cancel");
    public string BtnRemove => T("btn.remove");
    public string MenuPlay => T("menu.play");
    public string MenuRename => T("menu.rename");
    public string MenuOpenUrl => T("menu.openUrl");
    public string MenuPause => T("menu.pause");
    public string MenuResume => T("menu.resume");
    public string MenuCancel => T("menu.cancel");
    public string MenuRemove => T("menu.remove");
    public string SettingsTitle => T("settings.title");
    public string SettingsSavePath => T("settings.savePath");
    public string SettingsMaxConcurrent => T("settings.maxConcurrent");
    public string SettingsRetry => T("settings.retry");
    public string SettingsLogLevel => T("settings.logLevel");
    public string SettingsAutoRecover => T("settings.autoRecover");
    public string SettingsSave => T("settings.save");
    public string SettingsClose => T("settings.close");
    public string DialogTitle => T("dialog.title");
    public string QueueTooltip => T("queue.tooltip");

    private static readonly Dictionary<string, (string Zh, string En)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nav.back"] = ("后退", "Back"),
        ["nav.forward"] = ("前进", "Forward"),
        ["nav.go"] = ("访问", "Go"),
        ["nav.probe"] = ("探测", "Probe"),
        ["nav.newTab"] = ("新标签", "New"),
        ["nav.settings"] = ("设置", "Settings"),
        ["tip.back"] = ("后退", "Go back"),
        ["tip.forward"] = ("前进", "Go forward"),
        ["tip.go"] = ("打开地址", "Open address"),
        ["tip.probe"] = ("手动触发完整探测（不刷新网页）", "Run full probe without reloading"),
        ["tip.newTab"] = ("新标签", "New tab"),
        ["tip.settings"] = ("设置", "Settings"),
        ["tip.language"] = ("切换到英文", "Switch to Chinese"),
        ["side.detected"] = ("已发现视频", "Detected videos"),
        ["side.queue"] = ("下载队列", "Download queue"),
        ["mode.video"] = ("视频", "Video"),
        ["mode.audio"] = ("音轨", "Audio"),
        ["btn.download"] = ("下载", "Download"),
        ["btn.pause"] = ("暂停", "Pause"),
        ["btn.resume"] = ("恢复", "Resume"),
        ["btn.cancel"] = ("取消", "Cancel"),
        ["btn.remove"] = ("移除", "Remove"),
        ["menu.play"] = ("播放", "Play"),
        ["menu.rename"] = ("重命名", "Rename"),
        ["menu.openUrl"] = ("打开网址", "Open page"),
        ["menu.pause"] = ("暂停", "Pause"),
        ["menu.resume"] = ("恢复", "Resume"),
        ["menu.cancel"] = ("取消", "Cancel"),
        ["menu.remove"] = ("移除", "Remove"),
        ["settings.title"] = ("设置", "Settings"),
        ["settings.savePath"] = ("保存目录", "Save folder"),
        ["settings.maxConcurrent"] = ("最大并发", "Max concurrent"),
        ["settings.retry"] = ("重试次数", "Retry count"),
        ["settings.logLevel"] = ("日志级别", "Log level"),
        ["settings.autoRecover"] = ("启动时自动恢复已暂停的下载", "Auto-resume paused downloads on startup"),
        ["settings.save"] = ("保存", "Save"),
        ["settings.close"] = ("关闭", "Close"),
        ["settings.invalidPath"] = ("保存目录无效，请填写绝对路径。", "Invalid save folder. Use an absolute path."),
        ["settings.invalidConcurrent"] = ("最大并发必须是数字。", "Max concurrent must be a number."),
        ["settings.invalidRetry"] = ("重试次数必须是数字。", "Retry count must be a number."),
        ["settings.saved"] = ("设置已保存。并发数变更需重启应用后生效。", "Settings saved. Concurrent changes need an app restart."),
        ["dialog.title"] = ("提示", "Notice"),
        ["dialog.removeTitle"] = ("移除下载任务", "Remove download"),
        ["dialog.removeBody"] = ("请选择移除方式：\n\n是 — 删除记录并删除已下载文件\n否 — 仅删除记录，保留文件\n取消 — 不移除",
            "Choose how to remove:\n\nYes — delete record and file\nNo — delete record only\nCancel — keep both"),
        ["status.noTab"] = ("没有可用的浏览器标签页。", "No browser tab available."),
        ["status.navFailed"] = ("导航失败：{0}", "Navigation failed: {0}"),
        ["status.emptyUrl"] = ("请输入有效的网址。", "Enter a valid URL."),
        ["status.invalidUrl"] = ("网址格式无效，请输入 http/https 地址。", "Invalid URL. Use http/https."),
        ["detect.drm"] = ("受 DRM 保护，不支持下载", "DRM protected; download not supported"),
        ["detect.parsing"] = ("解析中...", "Parsing..."),
        ["detect.generic"] = ("已使用通用探测", "Using generic probe"),
        ["status.probeNoPage"] = ("无法探测：当前没有有效页面。", "Cannot probe: no valid page."),
        ["status.probeManual"] = ("已手动触发探测（DOM + 外置解析）。", "Manual probe started (DOM + external)."),
        ["status.probeManualDone"] = ("手动探测完成：{0} 个结果", "Manual probe done: {0} result(s)"),
        ["status.probeManualEmpty"] = ("手动探测完成：暂未发现可下载地址（请先播放视频再点探测）",
            "Manual probe done: no downloadable URL (play the video, then probe)"),
        ["status.probeManualEmptyExt"] = ("手动探测完成：未发现地址。外置解析：{0}",
            "Manual probe done: no URL. External: {0}"),
        ["status.probeManualFailed"] = ("手动探测失败：{0}", "Manual probe failed: {0}"),
        ["status.probeWaiting"] = ("探测中：等待页面媒体加载…", "Probing: waiting for page media…"),
        ["status.probeRunning"] = ("探测中：DOM + 外置解析…", "Probing: DOM + external…"),
        ["status.probeDone"] = ("探测完成：{0} 个结果", "Probe done: {0} result(s)"),
        ["status.probeEmpty"] = ("探测完成：未发现可下载地址（请先播放视频，或点「探测」重试）",
            "Probe done: no downloadable URL (play video or retry Probe)"),
        ["status.probeEmptyExt"] = ("探测完成：未发现地址。外置解析：{0}", "Probe done: no URL. External: {0}"),
        ["status.probeFailed"] = ("探测失败：{0}", "Probe failed: {0}"),
        ["status.probeFound"] = ("已发现：{0} 个结果，正在验证", "Found: {0} result(s), validating"),
        ["status.contextWarn"] = ("页面上下文刷新警告：{0}", "Page context refresh warning: {0}"),
        ["status.cancelled"] = ("已取消下载并清理临时文件。", "Download cancelled; temp files cleaned."),
        ["status.removedBoth"] = ("已移除记录并删除文件。", "Record and file removed."),
        ["status.removedRecord"] = ("已移除下载记录。", "Download record removed."),
        ["status.renameEmpty"] = ("名称不能为空，已取消重命名。", "Name cannot be empty; rename cancelled."),
        ["status.renamed"] = ("已重命名为：{0}", "Renamed to: {0}"),
        ["status.renameFailed"] = ("重命名失败：{0}", "Rename failed: {0}"),
        ["status.playIncomplete"] = ("未下载完成，无法播放。", "Download not finished; cannot play."),
        ["status.fileMissing"] = ("文件不存在或已被删除。", "File missing or deleted."),
        ["status.played"] = ("已用系统默认播放器打开：{0}", "Opened with system player: {0}"),
        ["status.playFailed"] = ("无法播放：{0}", "Cannot play: {0}"),
        ["status.noPageUrl"] = ("该任务没有保存原始页面地址。", "This job has no saved page URL."),
        ["status.openedPage"] = ("已打开原始页面：{0}", "Opened original page: {0}"),
        ["status.webviewInitFailed"] = ("浏览器初始化失败：{0}。地址栏仍可输入网址，请检查 WebView2 Runtime。",
            "Browser init failed: {0}. Address bar still works; check WebView2 Runtime."),
        ["status.demoBody"] = ("本机 ID 是 {0}，请联系管理员获取正式版本。\nDEMO 版本仅可下载不超过 10 MiB 的视频或音频文件。",
            "Machine ID: {0}. Contact admin for a full license.\nDEMO can download files up to 10 MiB only."),
        ["title.full"] = ("Video Downloader - 正版用户", "Video Downloader - Licensed"),
        ["title.demo"] = ("Video Downloader - DEMO用户", "Video Downloader - DEMO"),
        ["job.removed"] = ("已移除", "Removed"),
        ["job.completed"] = ("已完成", "Completed"),
        ["job.muxing"] = ("正在封装", "Muxing"),
        ["job.downloading"] = ("下载中", "Downloading"),
        ["job.pending"] = ("排队中", "Queued"),
        ["job.preparing"] = ("准备中", "Preparing"),
        ["job.paused"] = ("已暂停", "Paused"),
        ["job.cancelled"] = ("已取消", "Cancelled"),
        ["job.failed"] = ("失败", "Failed"),
        ["job.fileDeleted"] = ("文件已删除", "File deleted"),
        ["queue.tooltip"] = ("右键打开任务菜单；双击或右键「播放」均用系统默认播放器打开已完成项",
            "Right-click for menu; double-click or Play opens completed files with the system player"),
        ["dialog.playIncomplete"] = ("下载尚未完成，无法播放。", "Download not finished; cannot play."),
        ["dialog.fileMissing"] = ("文件不存在或已被删除。", "File missing or deleted."),
        ["tab.new"] = ("新标签页", "New tab"),
        ["preset.google"] = ("谷歌", "Google"),
        ["preset.youtube"] = ("YouTube", "YouTube"),
        ["preset.douyin"] = ("抖音", "Douyin"),
        ["preset.tiktok"] = ("TikTok", "TikTok"),
        ["preset.bilibili"] = ("B站", "Bilibili"),
    };
}
