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
        OnPropertyChanged(nameof(NavAuto));
        OnPropertyChanged(nameof(NavAutoStop));
        OnPropertyChanged(nameof(NavNewTab));
        OnPropertyChanged(nameof(NavLocal));
        OnPropertyChanged(nameof(NavSettings));
        OnPropertyChanged(nameof(TipBack));
        OnPropertyChanged(nameof(TipForward));
        OnPropertyChanged(nameof(TipGo));
        OnPropertyChanged(nameof(TipProbe));
        OnPropertyChanged(nameof(TipAuto));
        OnPropertyChanged(nameof(TipNewTab));
        OnPropertyChanged(nameof(TipLocal));
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
        OnPropertyChanged(nameof(BtnGroup));
        OnPropertyChanged(nameof(BtnUngroup));
        OnPropertyChanged(nameof(MenuPlay));
        OnPropertyChanged(nameof(MenuRename));
        OnPropertyChanged(nameof(MenuOpenFolder));
        OnPropertyChanged(nameof(MenuOpenUrl));
        OnPropertyChanged(nameof(MenuPause));
        OnPropertyChanged(nameof(MenuResume));
        OnPropertyChanged(nameof(MenuCancel));
        OnPropertyChanged(nameof(MenuRemove));
        OnPropertyChanged(nameof(SettingsTitle));
        OnPropertyChanged(nameof(SettingsSavePath));
        OnPropertyChanged(nameof(SettingsMigrate));
        OnPropertyChanged(nameof(SettingsMigrateTip));
        OnPropertyChanged(nameof(SettingsMaxConcurrent));
        OnPropertyChanged(nameof(SettingsRetry));
        OnPropertyChanged(nameof(SettingsFailedRetryInterval));
        OnPropertyChanged(nameof(SettingsFailedRetryIntervalTip));
        OnPropertyChanged(nameof(SettingsLogLevel));
        OnPropertyChanged(nameof(SettingsEnableLogging));
        OnPropertyChanged(nameof(SettingsEnableLoggingTip));
        OnPropertyChanged(nameof(SettingsClearLogs));
        OnPropertyChanged(nameof(SettingsClearLogsTip));
        OnPropertyChanged(nameof(SettingsOpenLogsFolder));
        OnPropertyChanged(nameof(SettingsOpenLogsFolderTip));
        OnPropertyChanged(nameof(SettingsAutoRecover));
        OnPropertyChanged(nameof(SettingsAppVersion));
        OnPropertyChanged(nameof(SettingsCopyright));
        OnPropertyChanged(nameof(SettingsCheckUpdate));
        OnPropertyChanged(nameof(SettingsSave));
        OnPropertyChanged(nameof(SettingsClose));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(QueueTooltip));
    }

    public string NavBack => T("nav.back");
    public string NavForward => T("nav.forward");
    public string NavGo => T("nav.go");
    public string NavProbe => T("nav.probe");
    public string NavAuto => T("nav.auto");
    public string NavAutoStop => T("nav.autoStop");
    public string NavNewTab => T("nav.newTab");
    public string NavLocal => T("nav.local");
    public string NavSettings => T("nav.settings");
    public string TipBack => T("tip.back");
    public string TipForward => T("tip.forward");
    public string TipGo => T("tip.go");
    public string TipProbe => T("tip.probe");
    public string TipAuto => T("tip.auto");
    public string TipNewTab => T("tip.newTab");
    public string TipLocal => T("tip.local");
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
    public string BtnGroup => T("btn.group");
    public string BtnUngroup => T("btn.ungroup");
    public string MenuPlay => T("menu.play");
    public string MenuRename => T("menu.rename");
    public string MenuOpenFolder => T("menu.openFolder");
    public string MenuOpenUrl => T("menu.openUrl");
    public string MenuPause => T("menu.pause");
    public string MenuResume => T("menu.resume");
    public string MenuCancel => T("menu.cancel");
    public string MenuRemove => T("menu.remove");
    public string SettingsTitle => T("settings.title");
    public string SettingsSavePath => T("settings.savePath");
    public string SettingsMigrate => T("settings.migrate");
    public string SettingsMigrateTip => T("settings.migrateTip");
    public string SettingsMaxConcurrent => T("settings.maxConcurrent");
    public string SettingsRetry => T("settings.retry");
    public string SettingsFailedRetryInterval => T("settings.failedRetryInterval");
    public string SettingsFailedRetryIntervalTip => T("settings.failedRetryIntervalTip");
    public string SettingsLogLevel => T("settings.logLevel");
    public string SettingsEnableLogging => T("settings.enableLogging");
    public string SettingsEnableLoggingTip => T("settings.enableLoggingTip");
    public string SettingsClearLogs => T("settings.clearLogs");
    public string SettingsClearLogsTip => T("settings.clearLogsTip");
    public string SettingsOpenLogsFolder => T("settings.openLogsFolder");
    public string SettingsOpenLogsFolderTip => T("settings.openLogsFolderTip");
    public string SettingsAutoRecover => T("settings.autoRecover");
    public string SettingsAppVersion => T("settings.appVersion");
    public string SettingsCopyright => T("settings.copyright");
    public string SettingsCheckUpdate => T("settings.checkUpdate");
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
        ["nav.auto"] = ("自动", "Auto"),
        ["nav.autoStop"] = ("停止", "Stop"),
        ["nav.newTab"] = ("新标签", "New"),
        ["nav.local"] = ("本地", "Local"),
        ["nav.settings"] = ("设置", "Settings"),
        ["tip.back"] = ("后退", "Go back"),
        ["tip.forward"] = ("前进", "Go forward"),
        ["tip.go"] = ("打开地址", "Open address"),
        ["tip.probe"] = ("重置全部探测状态并重新探测（不刷新网页）", "Reset all probe state and re-detect without reloading"),
        ["tip.auto"] = ("自动下载已发现视频；↓ / Shift+N / ] 切下一条；探测仍由页面切换触发；到达执行时长后停止", "Auto-download detected videos; ↓ / Shift+N / ] next; probing stays nav-driven; stops at session duration"),
        ["tip.newTab"] = ("新标签", "New tab"),
        ["auto.delayTitle"] = ("自动模式", "Auto mode"),
        ["auto.delayPrompt"] = ("自动模式执行时长（分钟，正整数）", "Auto session duration (minutes, positive integer)"),
        ["auto.delayHint"] = ("从开始起运行指定分钟后结束自动（不再切换/入队）；进行中的下载会继续。", "Runs for the chosen minutes from start, then stops auto (no more switching/enqueue). In-progress downloads continue."),
        ["auto.delayInvalid"] = ("请输入正整数分钟。", "Enter a positive integer (minutes)."),
        ["auto.ok"] = ("开始", "Start"),
        ["auto.cancel"] = ("取消", "Cancel"),
        ["status.autoRunning"] = ("自动模式中…剩余约 {0} 分", "Auto mode… ~{0} min left"),
        ["status.autoWaitingSlot"] = ("自动模式：等待下载空位…剩余约 {0} 分", "Auto: waiting for download slot… ~{0} min left"),
        ["status.autoStoppedTimeout"] = ("自动模式已结束：到达执行时长", "Auto mode ended: session duration reached"),
        ["status.autoStopped"] = ("自动模式已停止", "Auto mode stopped"),
        ["status.autoLocalDenied"] = ("本地视频页不能使用自动模式。", "Auto mode is not available on local library pages."),
        ["status.autoStarted"] = ("已进入自动模式（运行 {0} 分钟）", "Auto mode on (run {0} min)"),
        ["status.autoSkipLive"] = ("自动模式：检测到直播，已跳过", "Auto: live stream detected, skipped"),
        ["status.autoDelayDownload"] = ("自动模式：即将应用下载，{0} 秒…", "Auto: applying download in {0}s…"),
        ["status.autoDelaySwitch"] = ("自动模式：下载已开始，{0} 秒后切下一条…", "Auto: download started, next in {0}s…"),
        ["status.autoNoAddress"] = ("自动模式：本条暂无地址，{0} 秒后切下一条…", "Auto: no address on this item, next in {0}s…"),
        ["tip.local"] = ("打开本地视频库", "Open local video library"),
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
        ["btn.group"] = ("分组", "Group"),
        ["btn.ungroup"] = ("单列", "List"),
        ["menu.play"] = ("播放", "Play"),
        ["menu.rename"] = ("重命名", "Rename"),
        ["menu.openFolder"] = ("打开所在目录", "Open containing folder"),
        ["menu.openUrl"] = ("打开网址", "Open page"),
        ["menu.pause"] = ("暂停", "Pause"),
        ["menu.resume"] = ("恢复", "Resume"),
        ["menu.cancel"] = ("取消", "Cancel"),
        ["menu.remove"] = ("移除", "Remove"),
        ["settings.title"] = ("设置", "Settings"),
        ["settings.savePath"] = ("保存目录", "Save folder"),
        ["settings.migrate"] = ("迁移", "Migrate"),
        ["settings.migrateTip"] = ("将已完成下载迁移到当前保存目录并删除原文件", "Move completed downloads to the current save folder and delete originals"),
        ["settings.migrateConfirm"] = ("将把 {0} 个已完成文件迁移到当前保存目录，并删除原文件。进行中的下载须先停止。是否继续？", "Migrate {0} completed file(s) to the current save folder and delete originals. Stop in-progress downloads first. Continue?"),
        ["settings.migrateNone"] = ("没有需要迁移的已完成文件。", "No completed files need migration."),
        ["settings.migrateBlocked"] = ("存在进行中的下载任务，请先暂停或取消后再迁移。", "Downloads are in progress. Pause or cancel them before migrating."),
        ["settings.migrateInvalidPath"] = ("保存目录无效，无法迁移。", "Save folder is invalid; cannot migrate."),
        ["settings.migrateDone"] = ("迁移完成：成功 {0}，跳过 {1}，失败 {2}。", "Migration done: moved {0}, skipped {1}, failed {2}."),
        ["settings.migrateFailed"] = ("迁移失败：{0}", "Migration failed: {0}"),
        ["settings.maxConcurrent"] = ("最大并发", "Max concurrent"),
        ["settings.retry"] = ("重试次数", "Retry count"),
        ["settings.failedRetryInterval"] = ("失败捞起间隔(秒)", "Failed retry interval (sec)"),
        ["settings.failedRetryIntervalTip"] = ("失败任务每隔多少秒自动恢复；0 关闭。默认 10。",
            "Seconds between auto-resume of failed jobs; 0 disables. Default 10."),
        ["settings.logLevel"] = ("日志级别", "Log level"),
        ["settings.enableLogging"] = ("启用日志", "Enable logging"),
        ["settings.enableLoggingTip"] = ("开启后写入应用日志与诊断日志，便于调试；关闭则不再落盘。",
            "When on, writes app and diagnostic logs for debugging; when off, nothing is written to disk."),
        ["settings.clearLogs"] = ("清理日志", "Clear logs"),
        ["settings.clearLogsTip"] = ("删除本地全部应用日志与诊断日志文件。", "Delete all local app and diagnostic log files."),
        ["settings.clearLogsConfirm"] = ("确定清理全部日志文件？此操作不可恢复。",
            "Clear all log files? This cannot be undone."),
        ["settings.clearLogsDone"] = ("已清理 {0} 个日志文件。", "Cleared {0} log file(s)."),
        ["settings.clearLogsPartial"] = ("已清理 {0} 个，{1} 个未能删除（可能仍被占用）。",
            "Cleared {0}; {1} could not be deleted (may still be in use)."),
        ["settings.openLogsFolder"] = ("打开日志所在文件夹", "Open logs folder"),
        ["settings.openLogsFolderTip"] = ("在资源管理器中打开应用日志目录。", "Open the app logs directory in Explorer."),
        ["settings.openLogsFolderDone"] = ("已打开日志目录：{0}", "Opened logs folder: {0}"),
        ["settings.openLogsFolderFailed"] = ("无法打开日志目录：{0}", "Cannot open logs folder: {0}"),
        ["settings.autoRecover"] = ("启动时自动恢复已暂停的下载（仅启动时执行一次）", "Auto-resume paused downloads once on startup"),
        ["settings.appVersion"] = ("应用版本", "App version"),
        ["settings.copyright"] = ("版权", "Copyright"),
        ["settings.checkUpdate"] = ("检查更新", "Check for updates"),
        ["settings.save"] = ("保存", "Save"),
        ["settings.close"] = ("关闭", "Close"),
        ["settings.invalidPath"] = ("保存目录无效，请填写绝对路径。", "Invalid save folder. Use an absolute path."),
        ["settings.invalidConcurrent"] = ("最大并发必须是数字。", "Max concurrent must be a number."),
        ["settings.invalidRetry"] = ("重试次数必须是数字。", "Retry count must be a number."),
        ["settings.invalidFailedRetryInterval"] = ("失败捞起间隔必须是数字。", "Failed retry interval must be a number."),
        ["settings.saved"] = ("设置已保存。日志开关立即生效；并发数变更需重启应用后生效。",
            "Settings saved. Logging applies immediately; concurrent changes need an app restart."),
        ["update.checking"] = ("正在检查更新…", "Checking for updates…"),
        ["update.foundVersion"] = ("已发现新版本 {0}", "New version found: {0}"),
        ["update.downloadingFile"] = ("正在下载：{0}", "Downloading: {0}"),
        ["update.alreadyRunning"] = ("更新检查已在进行中…", "An update check is already running…"),
        ["update.licenseRequired"] = ("仅正版用户可更新。", "Updates are available for licensed users only."),
        ["update.upToDate"] = ("当前已是最新版本（{0}）。", "You are on the latest version ({0})."),
        ["update.promptTitle"] = ("发现新版本", "Update available"),
        ["update.promptBody"] = ("新版本 {0} 已下载完成，需要关闭并重启以完成更新。是否立即重启？",
            "Version {0} has been downloaded. Close and restart to finish updating?"),
        ["update.readyLater"] = ("新版本 {0} 已就绪，下次确认后即可升级。", "Version {0} is ready; confirm later to apply."),
        ["update.restarting"] = ("正在重启以应用 {0}…", "Restarting to apply {0}…"),
        ["update.forbidden"] = ("服务器拒绝更新请求（需要有效正版许可）。", "Update rejected (valid full license required)."),
        ["update.failed"] = ("检查更新失败：{0}", "Update check failed: {0}"),
        ["update.applyFailed"] = ("上次自动升级失败：\n{0}", "Previous auto-update failed:\n{0}"),
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
        ["status.probeManual"] = ("已重置探测状态并重新探测…", "Probe state reset; re-detecting…"),
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
        ["status.enqueued"] = ("已加入队列：{0}", "Queued: {0}"),
        ["status.enqueueFailed"] = ("无法开始下载：{0}", "Cannot start download: {0}"),
        ["status.removedBoth"] = ("已移除记录并删除文件。", "Record and file removed."),
        ["status.removedRecord"] = ("已移除下载记录。", "Download record removed."),
        ["status.renameEmpty"] = ("名称不能为空，已取消重命名。", "Name cannot be empty; rename cancelled."),
        ["status.renamed"] = ("已重命名为：{0}", "Renamed to: {0}"),
        ["status.renameFailed"] = ("重命名失败：{0}", "Rename failed: {0}"),
        ["status.playIncomplete"] = ("未下载完成，无法播放。", "Download not finished; cannot play."),
        ["status.fileMissing"] = ("文件不存在或已被删除。", "File missing or deleted."),
        ["status.played"] = ("已用系统默认播放器打开：{0}", "Opened with system player: {0}"),
        ["status.playedLocal"] = ("已在本地视频页播放：{0}", "Playing in local library: {0}"),
        ["status.playFailed"] = ("无法播放：{0}", "Cannot play: {0}"),
        ["status.localLibraryUnavailable"] = ("本地视频服务未启动。", "Local video library is unavailable."),
        ["status.localLibraryNoProbe"] = ("本地视频页不进行地址探测。", "Local library pages do not run address probing."),
        ["status.noPageUrl"] = ("该任务没有保存原始页面地址。", "This job has no saved page URL."),
        ["status.openedPage"] = ("已打开原始页面：{0}", "Opened original page: {0}"),
        ["status.folderMissing"] = ("找不到该任务的保存目录。", "Containing folder is missing."),
        ["status.openedFolder"] = ("已打开所在目录：{0}", "Opened containing folder: {0}"),
        ["status.openFolderFailed"] = ("无法打开所在目录：{0}", "Cannot open containing folder: {0}"),
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
        ["error.NET_TIMEOUT"] = ("网络超时", "Network timeout"),
        ["error.HTTP_403"] = ("拒绝访问 (403)", "Access denied (403)"),
        ["error.HTTP_404"] = ("地址不存在 (404)", "Not found (404)"),
        ["error.RANGE_MISMATCH"] = ("续传校验失败", "Resume mismatch"),
        ["error.DISK_FULL"] = ("磁盘空间不足", "Disk full"),
        ["error.LICENSE_DEMO_LIMIT"] = ("DEMO 上限 10 MiB", "DEMO 10 MiB limit"),
        ["error.FFMPEG_FAILED"] = ("封装失败", "Mux failed"),
        ["error.FFMPEG_NOT_FOUND"] = ("找不到 ffmpeg", "ffmpeg not found"),
        ["error.CONTEXT_EXPIRED"] = ("地址已失效，请重新探测", "Address expired; probe again"),
        ["error.INCOMPLETE_DOWNLOAD"] = ("文件不完整", "Incomplete file"),
        ["error.FILE_IO"] = ("文件读写失败", "File I/O error"),
        ["error.PERMISSION_DENIED"] = ("没有写入权限", "Permission denied"),
        ["error.INVALID_FORMAT"] = ("格式无效", "Invalid format"),
        ["error.MSE_TRACK_NOT_DOWNLOADABLE"] = ("该地址不是完整视频", "MSE track not downloadable"),
        ["error.INVALID_SAVE_PATH"] = ("保存目录无效", "Invalid save folder"),
        ["error.UNEXPECTED_ERROR"] = ("未知错误", "Unexpected error"),
        ["queue.tooltip"] = ("Ctrl 点击多选；右键只作用于当前行；双击已完成项在本地视频页播放",
            "Ctrl+click to multi-select; right-click acts on that row; double-click plays in local library"),
        ["dialog.playIncomplete"] = ("下载尚未完成，无法播放。", "Download not finished; cannot play."),
        ["dialog.fileMissing"] = ("文件不存在或已被删除。", "File missing or deleted."),
        ["tab.new"] = ("新标签页", "New tab"),
        ["preset.google"] = ("谷歌", "Google"),
        ["preset.youtube"] = ("YouTube", "YouTube"),
        ["preset.douyin"] = ("抖音", "Douyin"),
        ["preset.tiktok"] = ("TikTok", "TikTok"),
        ["preset.bilibili"] = ("B站", "Bilibili"),
        ["preset.localVideos"] = ("本地视频", "Local videos"),
    };
}
