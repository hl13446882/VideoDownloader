# VideoDownloader 工作交接文档

> 最新进度请先阅读 [2026-09-07 探测重构与实站验收交接](D:/VideoDownloader/HANDOVER_20260907_探测重构与实站验收.md)。下方为 2026-09-05 历史记录，部分流程和发布布局已过时；当前实站验收尚未全部通过。

**日期：** 2026-09-05  
**仓库：** `D:\VideoDownloader`  
**当前发布包：** `D:\VideoDownloader\publish\VideoDownload\VideoDownloader.exe`  
**原则偏好：** 优先修**通用探测方法**（网络捕获 / DOM / ffprobe / yt-dlp），避免为单站堆定制策略。

---

## 1. 产品与架构现状

WPF + WebView2 视频下载器。探测主路径已收敛为单一管线：

| 层级 | 组件 | 路径 |
|------|------|------|
| UI | `MainViewModel` / `MainWindow` | `src/VideoDownloader.UI/` |
| 浏览器 | `WebView2Host`（CDP 网络、DOM 探针、Cookie） | `src/VideoDownloader.Infrastructure/Browser/` |
| 探测管线 | `UnifiedMediaPipeline`（**唯一**注入的 `IMediaDetectionPipeline`） | `src/VideoDownloader.Infrastructure/Detection/` |
| 外置解析 | `YtDlpResolver`（S1） | `src/VideoDownloader.Infrastructure/Sites/ExternalResolvers/` |
| 下载 | `DownloadEngine` + `DownloadBackendRouter`（DirectHttp / FFmpeg 多输入 / m3u8） | `src/VideoDownloader.Infrastructure/Download/` |
| DI | `ServiceCollectionExtensions` | 只注册 `UnifiedMediaPipeline` |

**探测会话（UI）：** `MainViewModel` 用稳定会话（`StartPageDetectionSession` / `RunStableDetectionSessionAsync`）：等待 → DOM+yt-dlp → DOM 复核 → DOM 终检。页面切换 / MediaSession 变更会 soft重启探测（注意 `clearUi: false`、`forceReplace`，避免 thrash）。

**发布布局：**

```
publish/VideoDownload/
  VideoDownloader.exe
  data/                 # 托管依赖 + WebView2Loader(x64)
  ffmpeg/               # ffmpeg.exe, ffprobe.exe
  tools/yt-dlp.exe
  M3u8/N_m3u8DL-RE.exe
```

用户数据 / Cookie 配置目录：

`%LocalAppData%\VideoDownloader\WebView2Data`

---

## 2. 本轮已完成的修复（方法级）

### 2.1 探测稳定性

- **同页会话 thrash：** 段请求不再驱动 MediaSession；短数字名 `.m4s` 才当 segment，DASH baseURL 的 `.m4s` 可保留。
- **页面身份：** `SamePage` / `NormalizePage` 只保留内容 ID 查询键（`v` / `modal_id` / `aweme_id` 等），去掉 `list` / `spm_id_from` 等噪声，避免 SPA 改 URL 后丢掉已捕获媒体。
- **跨页污染：** `Clear()` 清空 `_page`/`_title`；`ProcessAsync` 拒绝与当前页不同的迟到事件；导航时清 pending。
- **Cookie 崩溃：** WebView2 session cookie `Expires` → `ToCookieExpiry`，避免 `DateTimeOffset` 越界。

### 2.2 下载与音视频

- `MediaUrlNormalizer` 保留 `itag`/`mime`，避免音视频 URL 被合并成一条。
- `DownloadEngine` 走 `DownloadBackendRouter`（不再永远假 MPD + N_m3u8DL-RE）。
- YouTube SABR（`sabr=1` 且无 mime）不当作可下载 progressive。

### 2.3 yt-dlp / JSON（.NET 10）

- **根因：** .NET 10 上 `JsonElement.TryGetInt64` / `TryGetDouble` 对 JSON `null` **会抛异常**（不是返回 false）。
- **修复：** `Infrastructure/Json/JsonNumber.cs`，读写前先判断 `ValueKind == Number`。
- B 站 `filesize: null` 曾导致整次外置解析失败，现已修复。
- Cookie 文件：UTF-8 无 BOM、净化 tab/换行、`#HttpOnly_` 前缀；按页过滤域，YouTube 额外允许 `google.com` cookies。
- Cookie 采集：`RefreshContextSnapshot` 会合并关联域（YouTube↔Google、抖音、B 站）。
- 抖音 URL：`jingxuan?modal_id=` → `https://www.douyin.com/video/{id}`。
- YouTube：多 `player_client` 重试；失败后再试无 cookie；`--no-config` 避免用户 conf 里的 `-f` 干扰。

### 2.4 UI

- 地址栏可编辑 ComboBox + 预设：Google / YouTube / 抖音 / TikTok / B 站。

---

## 3. 已知问题（人工测重点）

| 站点 | 最新自动验收（约 10:44） | 说明 |
|------|--------------------------|------|
| **YouTube** | variants=0 | yt-dlp：bot / “page needs to be reloaded”；网络侧多为 SABR，难出 progressive。**需在内置浏览器登录后再探测。** |
| **抖音** | variants≥1，下载 HTTP 206 OK | 网络/DOM 能出地址；yt-dlp 常报需 fresh cookies（可忽略若网络已出链）。 |
| **B 站** | variants=1，下载 OK，切换 OK | yt-dlp + 网络均可；解析 null 问题已修。 |

**切换探测：** 自动验收里「Switch update」在抖音↔B 站已 PASS；全站 PASS 仍被 YouTube 拖死。

**下载：** 样例 Range 探测曾 PASS（抖音/B 站）。YouTube 签名 URL / 403 仍可能出现，优先用 yt-dlp 给出的非 SABR 地址 + 完整请求上下文。

---

## 4. 发布与日常命令

```powershell
# 完整发布 + 自动验收（失败不删包，exit≠0）
powershell -NoProfile -File D:\VideoDownloader\scripts\publish-beta.ps1

# 仅发布，跳过验收（人工测试用）
powershell -NoProfile -File D:\VideoDownloader\scripts\publish-beta.ps1 -SkipVerify

# 先停掉占用进程再发
Get-Process VideoDownloader, VideoDownloader.Verify -ErrorAction SilentlyContinue | Stop-Process -Force
```

发布脚本会：

1. `dotnet publish` UI（win-x64）与 N_m3u8DL-RE  
2. 组装 `publish/VideoDownload`（校验 WebView2Loader 为 x64）  
3. 默认调用 `scripts/verify-sites.ps1`（可用 `-SkipVerify` 跳过）

---

## 5. 自动化测试（站点探测验收）

### 5.1 目的

对固定 3 个样例 URL 自动检查：

1. **能否探测出地址**（UI `SelectedDetectedVideo.Variants.Count > 0`）  
2. **切换 URL 后探测是否更新**（主 URL 会话键不同）  
3. **已发现视频清晰度下拉是否自动更新**（真实 `MainViewModel`：`Variants` 指纹变化 + `SelectedVariant` 换源）  
4. **样例是否可下载**（HTTP Range 读一小段；不作为总失败条件）

### 5.2 样例 URL（写死在 Verify）

```
https://www.youtube.com/watch?v=xg3bB_5VT1U&list=RDxg3bB_5VT1U&start_radio=1
https://www.douyin.com/jingxuan?modal_id=7674994001811892857
https://www.bilibili.com/video/BV13s4k63Ew8/?spm_id_from=333.1007.tianma.2-2-5.click
```

也可命令行传入更多 `http(s)` 参数覆盖默认列表。

### 5.3 工程与入口

| 项 | 路径 |
|----|------|
| 验收工程 | `tools/VideoDownloader.Verify/` |
| 脚本 | `scripts/verify-sites.ps1` |
| 实现 | `tools/VideoDownloader.Verify/MainWindow.xaml.cs` |
| 报告 | `artifacts/vd-verify-latest.log` |

### 5.4 运行方式

```powershell
# 依赖：已有 publish\VideoDownload\tools\yt-dlp.exe
powershell -NoProfile -File D:\VideoDownloader\scripts\verify-sites.ps1

# 已编译过时可跳过 build
powershell -NoProfile -File D:\VideoDownloader\scripts\verify-sites.ps1 -SkipBuild
```

**行为概要：**

1. 构建 `VideoDownloader.Verify`（引用正式 UI，挂载真实 `MainViewModel`）  
2. 复用 `%LocalAppData%\VideoDownloader\WebView2Data`（与正式应用同一 Cookie 配置）  
3. 指向发布目录的 `yt-dlp` / `ffmpeg`  
4. 依次经 `NavigateCommand` 导航 → 播放轻推 → 等待稳定探测会话刷新 UI  
5. 断言 `DetectedVideos` / `Variants` / `SelectedVariant` 下拉已更新  
6. 汇总写日志并 `exit 0/2`

**退出码：**

| Code | 含义 |
|------|------|
| 0 | 三站均探测到 variants，主地址切换，且下拉自动更新通过 |
| 1 | 未处理异常 |
| 2 | Detect all / Switch update / Dropdown auto-update 任一未通过 |

下载样例失败只记日志（`Download sample: FAIL (or blocked)`），**不单独决定 exit 2**。

### 5.5 判定逻辑（便于改测试）

- **Detect all：** 每个 URL 的 UI `VariantCount > 0`  
- **Switch update：** 所有「探测成功」的结果两两相邻 `PrimaryUrl`（当前 `SelectedVariant`）经 `MediaUrlNormalizer.IsSameSession` 比较为不同  
- **Dropdown auto-update：**  
  - 首站：下拉有选中项  
  - 其后：`Variants` URL 指纹相对上一成功站变化，且 `SelectedVariant` 不再是旧会话地址  
- **Download sample：** 对当前选中变体 Range GET，200/206 且读到字节即 OK  

### 5.6 与正式应用的关系

- Verify **走** `MainViewModel`（含 `ReplaceVariants` / 稳定探测会话），因此下拉行为与正式 UI 同路径  
- 有播放/同意横幅的 JS 轻推  
- 窗口可见（非完全 headless），便于观察卡登录页的情况  

### 5.7 最新自动跑结果摘要

见 `artifacts/vd-verify-latest.log`（2026-09-05 ~10:44）：

- Detect all: **FAIL**（YouTube=0）  
- Switch update: **PASS**  
- Download sample: **PASS**（抖音/B 站）  

---

## 6. 建议的后续工作（按优先级）

1. **YouTube 探测**  
   - 确认内置浏览器登录态是否有效（Cookie 是否含登录相关项）。  
   - 网络路径：在仅有 SABR 时如何给出可下载地址（或强制依赖已登录 yt-dlp）。  
   - 减少无 cookie 重试噪音；失败时保留「带 cookie 的最后错误」而不是末次 bot 文案。  

2. **验收增强**  
   - YouTube 未登录时标记为 `SKIP` 而非整次 FAIL（可选策略）。  
   - 探测计数 / CDP 候选数写入日志，便于区分「没播起来」vs「过滤过严」。  

3. **清理**  
   - 临时目录 `tools/_parse_test` 仅为复现 B 站 JSON null 用，可删。  

4. **人工回归清单**（发布后）  
   - [ ] YouTube：登录后探测是否出音+视，下载是否成功  
   - [ ] 抖音：精选 modal 链接是否出地址；切换后列表是否更新  
   - [ ] B 站：BV 链接是否出地址；切换后是否更新  
   - [ ] 地址栏预设切换是否正常  
   - [ ] 连续切换三站无「旧站地址残留」  
 - [ ] 考虑文件名避免重复问题  

---

## 7. 关键文件速查

```
scripts/publish-beta.ps1          # 发布（-SkipVerify）
scripts/verify-sites.ps1          # 三站自动验收
tools/VideoDownloader.Verify/     # 验收宿主
artifacts/vd-verify-latest.log    # 最近一次验收日志

src/.../Detection/UnifiedMediaPipeline.cs
src/.../Detection/MediaUrlNormalizer.cs
src/.../Json/JsonNumber.cs
src/.../Sites/ExternalResolvers/YtDlpResolver.cs
src/.../Browser/WebView2Host.cs
src/.../Download/DownloadEngine.cs
src/.../Download/DownloadBackendRouter.cs
src/VideoDownloader.UI/ViewModels/MainViewModel.cs
```

---

## 8. 交接时注意

### 本轮续接更新（2026-09-05）

- 外置解析使用页面代次和调用方的联合取消令牌；等待结束后丢弃过期结果及错误，结果写入捕获的原代次字典。
- yt-dlp 取消时终止进程树并等待退出，单次解析限制为 30 秒；调用方取消不再转换成普通解析失败。
- 重试成功清除旧错误；所有尝试失败时优先保留带 Cookie 尝试的错误，避免匿名错误覆盖诊断线索。
- 新增 4 项取消/迟到结果回归用例。核心测试 26 项、基础设施测试 38 项通过，后者包含本地生成媒体的检测、刷新、分片下载、音轨和历史记录集成测试。
- 验收宿主已移除硬编码公开视频。脚本必须传 `-Urls`；发布后验收使用 `-VerifyUrls`，仅发布使用 `-SkipVerify`。验收脚本不再强制结束用户正在运行的程序。
- 本轮未进行四站实机验收，YouTube 登录相关问题仍待使用用户授权地址验证；不可将本地测试视为四站通过。
- 自动页面会话已改为：等待页面稳定后仅执行一次 DOM + 外置解析；不会在“已合并通用探测与外置解析”后继续执行定时复检。
- 播放器监听已区分真实 DOM 播放器源与 performance/network 资源。后两者仅补充候选地址，不会因 ABR/CDN 签名地址变化重启探测；真实播放器源变化才重新探测。
- 文件名优先组合发布者/频道与标题；落盘同名改用确定性 `_2`、`_3` 序号，避免覆盖并在队列中可辨识。

- **不要**为通过验收去删已发布包；`publish-beta` 在 verify 失败时只 WARNING + 非 0 退出。  
- 改探测逻辑后默认应跑 `verify-sites.ps1` 或完整 `publish-beta.ps1`。  
- 人工急测用 `-SkipVerify`。  
- 站点现象只当观测样本；优先改 G1–G7 / S1 方法，而不是再加站点 if。
