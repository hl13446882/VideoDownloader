# VideoDownloader 架构说明

更新日期：2026-09-08  
相关交接：[`HANDOFF_LIVE_ACCEPTANCE_20260908.md`](./HANDOFF_LIVE_ACCEPTANCE_20260908.md)

---

## 1. 目标与边界

本仓库是 **Windows WPF 视频下载器**：内嵌 WebView2 打开页面 → 统一探测媒体地址 → 用户选择变体 → 统一下载/合成。

硬约束：

- **一条探测管线、一条下载引擎**；站点差异只进适配器 / 观察脚本，不单独开站下载器。
- Cookie / 登录 **不是**默认修复手段。
- 旧的 `ISiteAdapter` + Core `MediaDetectionPipeline` **已不接线**；线上路径是 `UnifiedMediaPipeline` + `ISiteMediaAdapter`。

---

## 2. 解决方案分层

| 项目 | 职责 |
| --- | --- |
| `src/VideoDownloader.Core` | 模型、契约、纯逻辑（无 UI / 无 Windows 专用依赖） |
| `src/VideoDownloader.Infrastructure` | WebView2、探测、下载、FFmpeg、yt-dlp、仓储、许可、DI |
| `src/VideoDownloader.UI` | WPF：`MainViewModel`、浏览器标签、设置 |
| `tools/VideoDownloader.Verify` | `--live` / `--local` 真机验收 |
| `scripts/` | `publish-beta.ps1`、`verify-requested-sites.ps1` 等 |
| `N_m3u8DL-RE-main/` | HLS/DASH 分段下载器源码（运行时 `M3u8/N_m3u8DL-RE.exe`） |

运行时外部二进制（相对安装目录）：`ffmpeg/ffprobe.exe`、`M3u8/N_m3u8DL-RE.exe`、`tools/yt-dlp.exe`。

---

## 3. 总览

```
┌─────────────────────────────────────────────────────────────┐
│  UI (MainViewModel + BrowserTab + WebView2Host)             │
│    导航 / 切 feed / 选 DetectedVideo / Enqueue 下载          │
└───────────────┬───────────────────────────────┬─────────────┘
                │ CDP / 脚本 / ProbePage          │ Enqueue
                ▼                               ▼
┌───────────────────────────────┐   ┌─────────────────────────┐
│  NetworkEventNormalizer       │   │  DownloadEngine         │
│  Raw → NormalizedNetworkEvent │   │  BackendRouter → HTTP / │
└───────────────┬───────────────┘   │  FFmpeg / N_m3u8DL-RE   │
                ▼                   └─────────────────────────┘
┌───────────────────────────────┐
│  UnifiedMediaPipeline         │
│  + ISiteMediaAdapter(s)       │
│  + yt-dlp (可选外置解析)       │
│  → DetectedVideo / Variants   │
└───────────────────────────────┘
```

DI 入口：`Infrastructure/ServiceCollectionExtensions.cs`  
（`IMediaDetectionPipeline` → `UnifiedMediaPipeline`）。

---

## 4. 探测路径（LIVE）

### 4.1 浏览器层

**`Browser/WebView2Host.cs`**

- CDP：`Network.*`；`Target.setAutoAttach(flatten)` 尽量覆盖跨域播放器 iframe（MacCMS 等）。
- 注入：`VideoObservationScript`（身份 / 文案 / media / images）、`MediaAddressDiscoveryScript`、`FrameAddressDiscovery`（跨 frame 抽 URL）。
- 归一化：`INetworkEventNormalizer.NormalizeAsync` → `IDiscoveryScope.ProcessAsync`。
- 页面级：`ProbePageAsync(page, title, scriptJson, …)`；会话切换时 `Clear` / `ResetDetectionSession`。

### 4.2 管线核心

**`Detection/UnifiedMediaPipeline.cs`**（实现 `IMediaDetectionPipeline`）

1. **准入**：专用 `ISiteMediaAdapter.EvaluateNetworkCandidate` + `GenericMediaAdapter`；`CandidateDecisionPolicy` **站点非 Default 则站点意见优先**。
2. **形态学**：`IsCandidate`（扩展名 / mime / Media 类型 / playurl 等）；**无站点 host 白名单**。
3. **实播证据**：`IsBrowserPlayEvidence`（ResourceType=Media 或强 mime + 200/206）→ `BrowserObserved`，可跳过独立采样。
4. **排队探测**：`Queue` → ffprobe / manifest（`IManifestResolver`）；槽位限流。
5. **外置**：`IExternalSiteResolver`（yt-dlp）→ `ProbeSampleGate` 校验可访问性。
6. **聚合**：`BuildAggregatedVideo` / `MergeVideos` → `VideoDetected`；音视频按 `ContentIdentity` + `CanPair` 配对；相册见 §6。
7. **防串站**：页面 host 与 CDN host 明显不符时丢弃（如 B 站 upos 泄漏到 MacCMS 页）。

辅助：`MediaOwnership`、`MediaUrlNormalizer`、`MediaVariantRanking`、`MediaVariantReconciler`、`ProbeSampleGate`、`MediaAddressScanner`。

### 4.3 站点适配器（新）

契约：`Core/Contracts/SiteMediaAdapterContracts.cs`  
实现：`Infrastructure/Sites/MediaAdapters/`

| Adapter | 作用（不下载） |
| --- | --- |
| `GenericMediaAdapter` | 跨站形态学默认 |
| `DouyinMediaAdapter` | 抖音 CDN / identity / modal_id / note |
| `TikTokMediaAdapter` | 海外 CDN / 实播优先 / 403 策略 |
| `BilibiliMediaAdapter` | playurl / m4s / BV |
| `YouTubeMediaAdapter` | YouTube 专用提示 |

职责：`ResolveContentIdentity`、`EvaluateNetwork/DomCandidate`、`EnrichRequestContext`、`GetValidationPolicy`、`CanonicalizeExternalPageUrl`、`ScoreCandidate`。

### 4.4 遗留（勿再用）

未在 DI 接到 live 路径：

- `ISiteAdapter` / `SiteAdapterRouter` / `SiteProbeOrchestrator`
- `Sites/*SiteAdapter.cs`
- Core `MediaDetectionPipeline` / `MediaDetector`

写新功能请改 **UnifiedMediaPipeline + ISiteMediaAdapter**。

---

## 5. UI 如何接探测

**`UI/ViewModels/MainViewModel.cs`**

- 订阅 `VideoDetected` → 按 `SessionId` / 当前页相关性过滤 → 更新 `DetectedVideos`。
- 文档导航：`NavigationStarted` → `StartPageDetectionSession(clearUi: true)` → `pipeline.Clear()`。
- Feed 滑动：`MediaSessionChanged` → 新会话（可 `forceReplace`）。
- 下载：`DownloadEngine.EnqueueAsync(variant, name, pageUrl)`。

---

## 6. 关键模型

| 类型 | 要点 |
| --- | --- |
| `MediaTrack` | `Kind`: Video / Audio / Combined / Unknown / **Image**；`BrowserObserved`、`ContentIdentity`、`Evidence`、`Hls` |
| `MediaVariant` | 一组 Tracks + Container（含 `album`）+ RecoveryPageUrl |
| `DetectedVideo` | 页级结果：Title、Variants、SessionId、SiteContentId |
| `MediaEvidence` | Heuristic / DomObserved / ExternalResolved / BrowserObserved |
| `ContentIdentity` | 如 `content:douyin:…` / `id:BV…`；配对与 feed 归属用 |

相册变体：≥2 张图（`/note/` 可 ≥1）+ 一条 Audio → Container=`album`。

---

## 7. 下载路径

**`Download/DownloadBackendRouter.cs`** → `DownloadBackendKind`：

| Kind | 条件 | 实现 |
| --- | --- | --- |
| `DirectHttp` | 单轨渐进式 | `HttpMediaDownloader` |
| `FfmpegMultiInput` | 多轨分离 | 分轨下载 → `RunMultiInputRemuxAsync` |
| `FfmpegRemux` | HLS/DASH / audio-extract | 默认走 `M3u8DownloadAdapter`（N_m3u8DL-RE）等 |
| `FfmpegAlbumSlideshow` | Image 轨或 `album` | 下图+音 → `RunAlbumSlideshowAsync`（按时长均分静帧） |

**`DownloadEngine`**：队列、并发、持久化、失败重试、启动恢复；403 时可刷新 WebView Cookie；非实播 URL 才考虑 yt-dlp **续址**（`MediaAddressRenewal`）。

**yt-dlp**（`YtDlpResolver`）：可选「页 → 格式列表」；人机验证单独标记；**不是**探测主路径。

---

## 8. 相册合成（协议能力）

```
观察脚本 images[] + music URL
    → Pipeline._albumImages + Audio 轨
    → MediaVariant(container=album)
    → Backend = FfmpegAlbumSlideshow
    → ffprobe 音频时长 / N → 每张静帧等长 → libx264 + aac
```

抖音只负责发现；合成在统一 FFmpeg 后端。

---

## 9. 配置与发布

- 选项：`Infrastructure/Configuration/AppOptions.cs`（ffmpeg 路径、探测阈值等）。
- 发布：`scripts/publish-beta.ps1` → `publish/VideoDownload`，再同步安装目录 `D:\VideoDownload`。
- 分支惯例：功能合入 `clean-main` 后自动 publish/push（见 `.cursor/rules/auto-publish-push.mdc`）。

---

## 10. 验收与测试落点

| 层级 | 位置 |
| --- | --- |
| 单元测试 | `tests/VideoDownloader.*.Tests` |
| 脚本 hash 门禁 | `tests/verify-address-discovery.cjs` |
| 真机 live | `tools/VideoDownloader.Verify` + `scripts/verify-requested-sites.ps1` |
| 证据 | `artifacts/live-acceptance/runs/<runId>/` |

当前 live 差距见交接文档，不在此重复。

---

## 11. 扩展指南（简）

| 需求 | 改哪里 |
| --- | --- |
| 某站更容易收流 / 换 identity | 新或改 `ISiteMediaAdapter`，必要时观察脚本 |
| 新容器下载方式 | `DownloadBackendKind` + Router + Engine 分支（保持统一入口） |
| 通用站 iframe HLS | CDP auto-attach + FrameAddressDiscovery + `IsCandidate`；勿写站点专用下载器 |
| 文案/命名 | 观察脚本 + pipeline `_title`；命名构建器另有 hash 门禁 |

---

## 12. 相关文档

- `DOCS/PROBE_FLOW_20260908.md` — 探测时序细节  
- `DOCS/SITE_ADAPTER_PHASE1_20260908.md` — 适配器 Phase1  
- `DOCS/HANDOFF_LIVE_ACCEPTANCE_20260908.md` — 验收现状与差距  

---

*本文描述的是 2026-09-08 前后的 LIVE 架构；以 `ServiceCollectionExtensions` 注册的类型为准。*
