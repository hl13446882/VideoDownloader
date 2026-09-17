# 四站独占媒体探测 — 交接文档

> 更新：2026-09-09  
> 分支：`clean-main`  
> 已推送提交：`3a84451` — *Route Douyin/TikTok/YouTube/Bilibili through exclusive site detectors.*  
> **工作区另有未提交修复**（见文末「未提交改动」），live 全量验收未跑完。

---

## 1. 目标与硬约束

| 约束 | 说明 |
|------|------|
| 独占路由 | 进入页面先 `SiteDetectionRouter.Resolve(pageUrl)`，再启动**唯一** Detector |
| 四站互不共享探测逻辑 | Douyin / TikTok / YouTube / Bilibili 之间禁止共用 morph、browser-play、相册 JS、身份解析等 |
| 禁止 Generic fallback | 四站失败**不得**掉进 Unified/Generic |
| 下载仍统一 | 映射到现有 `DetectedVideo` → `DownloadEngine`（HLS/DASH/MP4/remux/相册 slideshow） |
| 多视频卡 | **仅 Generic**；四站每页只保留一张卡（通常最大/当前作品） |

产品验收补充（本轮用户剧情）：

1. 能探测到的大小、格式写入文案后缀（作文件名一部分）  
2. &lt;20MB 须下完；≥20MB 至少 20MB  
3. 四站单视频；通用站可多视频  
4. 多清晰度/格式应识别  
5. 禁止「已带音轨的视频 + 额外音轨」同时下载/remux  

---

## 2. 新架构

```text
WebView2Host
    │ CDP / Probe / Observation
    ▼
RoutedMediaDetectionPipeline  ← DI 注册为 IMediaDetectionPipeline
    │
    ├─ SiteDetectionRouter.Resolve(pageUrl)
    │
    ├─ Douyin     → DouyinMediaDetector      ──┐
    ├─ TikTok     → TikTokMediaDetector      ──┤ MediaDescriptor
    ├─ YouTube    → YouTubeMediaDetector     ──┤   (+ 可选 Formats 清晰度梯)
    ├─ Bilibili   → BilibiliMediaDetector    ──┤
    │                                         ▼
    │                              MediaDescriptorMapper
    │                                         ▼
    │                              DetectedVideo → UI / DownloadEngine
    │
    └─ Other → UnifiedMediaPipeline（仅 Generic；入口拒绝四站 host）
```

### 2.1 核心类型（Core）

| 路径 | 职责 |
|------|------|
| `src/VideoDownloader.Core/Models/MediaDescriptor.cs` | `SiteKind`、`MediaContentType`、`AlbumImageItem`、`MediaDescriptor`（含 `Formats`） |
| `src/VideoDownloader.Core/Contracts/ExclusiveSiteDetectionContracts.cs` | `ISiteDetectionRouter`、`IExclusiveSiteMediaDetector`、`IExclusiveSiteMediaDetectorResolver` |
| `src/VideoDownloader.Core/Sites/SiteDetectionRouter.cs` | Host → SiteKind；Detector 列表 Resolve |

### 2.2 路由与映射（Infrastructure）

| 路径 | 职责 |
|------|------|
| `Detection/RoutedMediaDetectionPipeline.cs` | 独占/Generic 分发；`[DetectionRouter] Site=… Exclusive=true GenericPipeline=Bypassed` 日志 |
| `Detection/MediaDescriptorMapper.cs` | Descriptor → DetectedVideo；文案追加 `1080p 6.8MB mp4`；Combined 不与第二音轨 remux |
| `Detection/UnifiedMediaPipeline.cs` | `IsExclusiveSiteHost` 入口拒绝；仅 Generic adapter |

### 2.3 四站 Detector

```text
Infrastructure/Detection/Sites/
  Douyin/   DouyinMediaDetector + DouyinInternals（Identity/Mode/Session，站内私有）
  TikTok/   TikTokMediaDetector
  YouTube/  YouTubeMediaDetector（网络仅 googlevideo；yt-dlp 在站内，Formats 清晰度）
  Bilibili/ BilibiliMediaDetector（同理 playurl/upos + yt-dlp Formats）
```

**抖音调用链（作品级）**

1. `Routed` → `DouyinMediaDetector.BeginSession`  
2. `DouyinIdentity` 解析 AwemeId  
3. `DouyinContentModeResolver` → Video | Album | Unknown  
4. Video → 网络/JSON playAddr；Album → `Images[]` + 可选 BGM（无 VideoUrl 不进 Generic）  
5. `Complete` → `MediaDescriptor` → Mapper → UI  

滚动换作品 / Video↔Album：Session `SwitchContent` 整表清理。

### 2.4 页面 Observation（按 host 分发，互不共用相册 helper）

| 脚本 | 说明 |
|------|------|
| `Browser/SiteObservationBootstrap.cs` | 按 `location.hostname` 注入 |
| `DouyinObservationScript` / `TikTokObservationScript` | 各自 album，**禁止**共享 `observeAlbumPost` |
| `YouTubeObservationScript` / `BilibiliObservationScript` | 站专属 |
| `GenericObservationScript` | 嵌套旧 `VideoObservationScript`，仅 Other |

`WebView2Host` 已改为注入 `SiteObservationBootstrap.Install`。

### 2.5 DI（`ServiceCollectionExtensions.cs`）

- 注册：Router、四 Detector、`RoutedMediaDetectionPipeline` as `IMediaDetectionPipeline`  
- `UnifiedMediaPipeline` 仍为 singleton（供 Routed 委托 Other）  
- 四站旧 `*MediaAdapter` **已 Obsolete 且未再注册**；仅留 `GenericMediaAdapter`

### 2.6 UI

- `MainViewModel`：四站 `PruneOtherVideosForPage`（单卡）  
- 文件名：`DownloadFileNameBuilder` 追加 height / size / container（未提交改动中）

---

## 3. 已推送 vs 未提交

### 已在 `origin/clean-main`（`3a84451`）

- 独占契约 + Router + Routed 管道 + 四 Detector 骨架与实现  
- Observation 拆分、DI、单测 `ExclusiveSiteDetectionTests`（19 条）  
- Unified 拒绝四站 host；旧 MediaAdapter 标 Obsolete  

### 工作区未提交（建议下任先审再 commit）

| 文件 | 意图 |
|------|------|
| `RoutedMediaDetectionPipeline.cs` | Complete 不再死等 CDP lease；同站换作品 ID 强制 `BeginSession`；`IsCompleted` 看 `_exclusiveCompleted` |
| `YouTubeMediaDetector.cs` / `BilibiliMediaDetector.cs` | 仅合法 CDN；yt-dlp → `Formats` 多清晰度 |
| `MediaDescriptor.cs` / `MediaDescriptorMapper.cs` | `Formats`；文案带大小格式；Combined 不双下音轨 |
| `DownloadFileNameBuilder.cs` | 文件名 stem 带 size/format |
| `MainViewModel.cs` | 四站单卡 prune |
| `YtDlpResolver.cs` | `WaitForExitAsync` 真正吃 30s 超时，避免挂死 |
| `LiveAcceptance.cs` | 单步 120s 硬超时 + 15s 进度日志；TikTok 6 滑/≥5 过；下载 proof 60s |

**已知卡点（未收尾）**

- YouTube 连续测第 3 条 `rKrq5V3GJWI` 时曾长时间无日志（疑 CompleteDiscovery lease / yt-dlp WaitForExit）。未提交修复针对此，**尚未用新二进制跑通 3 条 YT**。  
- TikTok 首页常探测到片源但独立 HTTP 403（cookie）；需 cookie  enrichment + 滑动 ≥5 下载证明。  
- live 全量（用户 12 URL 剧情）**未完成**。

---

## 4. 测试脚本与命令

### 4.1 单元测试（快）

```powershell
cd D:\VideoDownloader
dotnet test tests\VideoDownloader.Infrastructure.Tests\VideoDownloader.Infrastructure.Tests.csproj `
  --filter "FullyQualifiedName~ExclusiveSiteDetectionTests"
```

覆盖：Router 互斥、Unified 拒绝四站、Douyin Case1–8、TikTok/YT/Bili 正路径、Mapper 相册 container。

### 4.2 Live 验收工具

可执行文件：

```text
tools\VideoDownloader.Verify\bin\Release\net10.0-windows\VideoDownloader.Verify.exe
```

先编：

```powershell
dotnet build tools\VideoDownloader.Verify\VideoDownloader.Verify.csproj -c Release
```

证据目录：

```text
artifacts\vd-verify-latest.log
artifacts\live-acceptance\latest.txt
artifacts\live-acceptance\results.json
artifacts\live-acceptance\runs\<runId>\
```

日志关键字（应出现）：

```text
[DetectionRouter] Site=Douyin Detector=DouyinMediaDetector Exclusive=true GenericPipeline=Bypassed
[DetectionRouter] Site=TikTok ...
[DetectionRouter] Site=YouTube ...
[DetectionRouter] Site=Bilibili ...
[DetectionRouter] Site=Other Detector=UnifiedMediaPipeline Exclusive=false GenericPipeline=Active
```

### 4.3 包装脚本

| 脚本 | 用途 |
|------|------|
| `scripts\verify-requested-sites.ps1` | 内置一组 Douyin/TikTok/Generic URL，`--live`，最长约 40min |
| `scripts\verify-sites.ps1 -Urls ...` | 需先有 `publish\VideoDownload`；传授权 URL |
| `scripts\publish-beta.ps1 -SkipVerify` | 发布到 `publish\VideoDownload`（再 robocopy → `D:\VideoDownload`） |

### 4.4 推荐：按站分批（避免一次卡死）

**原则：单步超过 ~2 分钟无 `LIVE PASS/FAIL` / `LIVE wait` 进度 → 杀进程查该站，不要干等。**

```powershell
$exe = "D:\VideoDownloader\tools\VideoDownloader.Verify\bin\Release\net10.0-windows\VideoDownloader.Verify.exe"
$dir = Split-Path $exe

# YouTube（3）
Start-Process $exe -WorkingDirectory $dir -ArgumentList @(
  '--live',
  'https://www.youtube.com/watch?v=oe9rK1jzNbA&list=RDoe9rK1jzNbA&start_radio=1',
  'https://www.youtube.com/watch?v=Y_tPE3o5NWk&list=RDY_tPE3o5NWk&start_radio=1',
  'https://www.youtube.com/watch?v=rKrq5V3GJWI&list=RDrKrq5V3GJWI&start_radio=1'
)

# 抖音：推荐滚动(4) + 精选 + 相册
# https://www.douyin.com/?recommend=1
# https://www.douyin.com/jingxuan?modal_id=7660503158577286451
# https://v.douyin.com/9cY9PQIM6HY/

# TikTok：首页滚动 6，≥5 下载算站通过（LiveAcceptance 门禁，未提交）
# https://www.tiktok.com/

# B站
# https://www.bilibili.com/video/BV1xMtw6XEAu
# https://www.bilibili.com/video/BV1CXWuz3E7V/
# https://www.bilibili.com/video/BV1x8g56LEsz/

# Generic
# https://www.xmfyy.com/index.php/vod/play/id/290850/sid/1/nid/1.html   # AES clear-key
# https://ally.vkzxbprqm.cc/archives/274623/                             # 多视频卡
```

监视（PowerShell）：

```powershell
# 每 20s 看尾日志；同一行超过 120s 无更新则停
Get-Content D:\VideoDownloader\artifacts\vd-verify-latest.log -Wait -Tail 20
# 或
Stop-Process -Name VideoDownloader.Verify -Force
```

### 4.5 发布安装

```powershell
powershell -File D:\VideoDownloader\scripts\publish-beta.ps1 -SkipVerify
# 停掉占用后：
robocopy D:\VideoDownloader\publish\VideoDownload D:\VideoDownload /MIR /R:1 /W:1
git push origin clean-main   # 仅在 commit 之后
```

仓库规则：任务完成后应 commit → publish → robocopy → push（本轮 live 未收尾，**未提交改动勿盲目 push**）。

---

## 5. 架构问答（原计划 12 点）

1. **抖音进入后调用链**  
   `WebView2` → `Routed` → `DouyinMediaDetector`（bypass Unified）→ Mapper → UI/下载。

2. **ContentMode 判定**  
   `DouyinContentModeResolver`（observation JSON `album`/`images`/`media` + `/note/` 路径）。

3. **Video 模式**  
   当前 AwemeId；拒预加载他作、tiny MSE crumb（非 browser-play）；CDN/playAddr/JSON。

4. **Album 模式**  
   成功条件 `Images.Count>0`；Audio 可选；禁止无图去找 mp4 / Generic。

5. **相册图来源**  
   `DouyinObservationScript`（`__UNIVERSAL_DATA_FOR_REHYDRATION__` 等）+ 同 AwemeId 网络图。

6. **排序与去重**  
   作品 index；规范化 path 去重。

7. **排除**  
   avatar/logo/emoji/封面缩略等正则（Detector + Observation）。

8. **BGM**  
   Album 下 music/play_url 与网络 audio，绑当前 ContentId。

9. **Video↔Album Session**  
   `DouyinDetectionSession.SwitchContent` 清空 Video/Audio/Images。

10. **Douyin→Generic fallback**  
    **无**（失败只记 `Failed`，PageProbed 空列表）。

11. **Douyin 页 Generic Observer**  
    **无**（Bootstrap 按 host 只装 Douyin 脚本）。

12. **自动化**  
    `ExclusiveSiteDetectionTests` 已绿；live 见上文未完成项。

---

## 6. 建议下任顺序

1. **提交并发布**当前未提交修复（或先只 cherry-pick Routed/YtDlp 防挂死）。  
2. **只跑 YouTube 3 条**，确认第 3 条 2 分钟内有 PASS/FAIL。  
3. 抖音推荐 4 + 精选 + 相册。  
4. TikTok：修 cookie/403，再跑 6 滑 ≥5。  
5. B站 3 条（依赖 yt-dlp Formats）。  
6. Generic：AES + `274623` 多视频（确认四站改动未误伤）。  
7. 全绿后 `commit` / `publish-beta -SkipVerify` / robocopy / `push origin/clean-main`。

---

## 7. 相关路径速查

```text
计划（勿改）：C:\Users\lzd11\.cursor\plans\exclusive_site_detectors_855f28a7.plan.md
对话：agent-transcripts / 06de23e4-98bb-462a-b73c-a6066d6dc9f9
安装目录：D:\VideoDownload
发布目录：D:\VideoDownloader\publish\VideoDownload
```

## 8. 2026-09-09 续接记录

### 已知验收缺口修复（实站仍待执行）

- `DownloadAcceptance.cs` 新增真实 `IDownloadEngine` 验收，替代独立 HTTP Range/skip-merge 下载证明。已知小于 20MiB 的输入必须完成；相册始终必须完成。完成文件通过 ffprobe 检查视频和所需音频流，并将流信息留在该次报告目录。大文件达到 20MiB 可停止，下载过程每 15 秒记录进度，单项最多 5 分钟。
- 删除旧下载证明方法，避免把单个音频、未合并分片或其他清晰度的成功误记为所选视频成功。
- 增加卡片数量、Combined+Audio、文案及文件名大小/格式检查。YouTube/Bilibili 测试要求至少两个不同清晰度/容器/视频编码组合；其他站记录格式数量，不能据此证明已穷尽源站格式。
- 文件名兜底分支补齐大小/格式，MKV/MKA 也保留格式标识。增加长标题、空标题及三种容器回归测试。
- 验证：Core 48 条通过；独占探测 21 条通过；Verify Release 构建 0 警告、0 错误；diff check 通过。
- 未运行修改后的 GUI/live 验收；此前进程启动被策略拒绝。TikTok cookie/403、实站格式完整度、真实合并结果仍待实测，不可将本记录当作全站 PASS。整体任务仍未收尾，尚未发布或推送。

- 修复 `RoutedMediaDetectionPipeline.CompleteDiscoveryAsync` 跨会话竞态：捕获原始 `DetectionSession`；Detector 完成后若已切换会话则退出；lease 等待及取消只操作原始会话，避免旧回调封闭新作品。
- 新增两条回归用例，分别覆盖 Detector 尚未完成时切换作品、等待旧 lease 时切换作品。独占测试合计 21 条全部通过。
- Verify Release 重新构建成功，0 错误；完整重编暴露 LiveAcceptance 现有 5 条可空性警告。`git diff --check` 通过。
- 尝试以隐藏窗口启动交接文档的 YouTube 三 URL live 批次时，自动审批审查拒绝执行，原因仅为 `blocked by policy`。该批次未启动，不能声称实站通过，也没有绕过拒绝重试。
- 全量实站验收仍未完成，尚未提交、发布或推送；保留全部原始未提交修改与本次修复。下一步仍需运行 YouTube 三条及后续各站验收，解决 TikTok 403，并完成验收后发布流程。
