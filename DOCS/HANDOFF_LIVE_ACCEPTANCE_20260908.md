# 交接：直播验收流程与当前差距（2026-09-08）

## 1. 结论（先看）

| 项 | 状态 |
| --- | --- |
| 整包 live 验收 | **未全绿**（最新一轮 14/14，**10 PASS / 4 FAIL**） |
| 抖音相册短链 | **已绿**（`v.douyin.com/9cY9PQIM6HY/` → note + 图+音） |
| B 站 ×3 | **已绿**（本轮） |
| TikTok ×4 | **已绿**（本轮） |
| 抖音推荐 ×4 | **1 绿 / 3 红**（多为「仅视频、无音轨配对」） |
| 抖音精选 modal | **已绿**（本轮落在相册变体） |
| 通用 xmfyy AES/解析站 | **仍红**（采不到可用媒体地址） |

已推送基线：`a4b0647`（`clean-main`）。  
此后还有**未提交**本地改动（见 §6），含跨站泄漏过滤、相册判定收紧、验收脚本放宽等。

最新证据：`artifacts/live-acceptance/runs/20260908-162935-a6d2cdc7`（`allPassed=false`）。

---

## 2. 怎么跑测试

### 2.1 入口

| 方式 | 命令 / 入口 |
| --- | --- |
| 请求站点包 | `scripts/verify-requested-sites.ps1` |
| 底层 | `tools/VideoDownloader.Verify` + `--live <url...>` |
| 发布后再测 | 先 `scripts/publish-beta.ps1 -SkipVerify`，再跑 Verify（Verify 用 `publish/VideoDownload`） |

当前 `verify-requested-sites.ps1` URL 列表：

1. `https://www.douyin.com/?recommend=1` → **期望 4 条**（ArrowDown）
2. `https://www.douyin.com/jingxuan?modal_id=7660503158577286451` → 1
3. `https://v.douyin.com/9cY9PQIM6HY/` → 1（相册）
4. `https://www.tiktok.com/` → **期望 4 条**
5. B 站 `BV1xMtw6XEAu` / `BV1CXWuz3E7V` / `BV1x8g56LEsz` → 各 1
6. `https://www.xmfyy.com/index.php/vod/play/id/290850/sid/1/nid/1.html` → 1  

**合计 expectedCount = 14。**

### 2.2 单条 PASS 条件（`LiveAcceptance.cs`）

须同时满足：

- `completed`：pipeline `IsCompleted`
- `sampleOk`：优选视频轨可采样，且有音轨 / Combined / 相册(Image+Audio)；拒绝 FLV 主视频
- `stable`：settle 阶段 session 不乱跳（`switches≤1`）
- `switched`：第 0 步有 identity 或 sampleOk；后续步 session/identity 相对上一条变化
- `captionOk`：文案非空、非宿主名、非 `"视频"`、非裸文件名；抖音/TikTok 允许与 `document.title` 同源（含「- 抖音」后缀）
- `uniqueIdentity`：identity 不重复；弱 feed key（如 `https://www.douyin.com/[]`）改用 `title:` 去重

证据目录：`artifacts/live-acceptance/runs/<runId>/`（`results.json` + 截图）。

### 2.3 相关单元 / 门禁

- `dotnet test tests/VideoDownloader.Infrastructure.Tests` / `Core.Tests`
- `node tests/verify-address-discovery.cjs`（观察脚本 SHA256 门禁）

---

## 3. 本轮已落地的能力（产品侧）

勿再当作「未做」：

1. **抖音相册**：探测 note/images + BGM → `MediaTrackKind.Image` + Audio → `FfmpegAlbumSlideshow`（按时长均分静帧）
2. **站点适配器**：Douyin / TikTok / Bilibili / YouTube / Generic（`Sites/MediaAdapters/`）
3. **浏览器实播**：CDP Media/200/206 → `BrowserObserved`，减少独立 GET 403
4. **B 站**：`__playinfo__` / DASH baseUrl 注入观察脚本；本轮 3/3 过
5. **跨站泄漏防护（未提交）**：非 B 站页拒绝 bilivideo/upos；避免 xmfyy 吃到上一页 B 站 m4s
6. **相册误判收紧（未提交）**：推荐流封面 ≥2 张或 `/note/` 才当相册；避免单封面抢过真视频
7. **通用地址发现增强（未提交）**：`player_aaaa` / `MacPlayer` / parse iframe / qlplayer；Frame 发现始终跑

安装目录：`D:\VideoDownload`（与 `publish/VideoDownload` 同步）。

---

## 4. 当前差距（按失败项）

### 4.1 抖音推荐 #2/#3/#4 — **仅视频、无音轨**

- 现象：`sample` 只有 `Video: … douyinvod.com`；无 Audio / Combined 完整对。
- 根因候选：
  - 浏览器只实播到视频 CDN；BGM/music URL 未进 Queue 或未通过校验
  - `CanPair` / identity：弱 key `https://www.douyin.com/[]` 与 `content:<aweme_id>` 混用，音视频一侧缺 stamp
  - yt-dlp 常报 `Fresh cookies … needed`，不能当默认修复手段
- 验收表现：文案/切换往往已 OK，卡在 **sampleOk（缺音）**

### 4.2 通用站 xmfyy #14 — **仍采不到地址**

- 页面事实（浏览器实测）：
  - `player_aaaa.url` = 腾讯页 `v.qq.com/...`（不是直链 m3u8）
  - `MacPlayer.Parse` = `https://svip.qlplayer.cyou/?url=`
  - **真实 m3u8 在跨域解析 iframe 内**加载；顶层 HTML 无 `.m3u8`
- 现状：`Unsupported URL`（yt-dlp）+ pipeline「未发现可用流」；`identity`/`caption` 空 → `switched=false` `captionOk=false`
- 缺口：
  1. OOPIF/`Target.setAutoAttach` 是否稳定收到 iframe 内 m3u8 Network 事件（需再验日志）
  2. 仅采集到 parse/腾讯入口不够，`IsCandidate`/校验要把「解析后 HLS」收进来
  3. 验收需关公告弹窗并等 iframe；脚本已加等待，但仍不够稳

### 4.3 次要 / 已缓解

| 问题 | 状态 |
| --- | --- |
| 相册短链 | 已过 |
| B 站仅视频 / 412 | 本轮已过（playinfo + 浏览器轨） |
| TikTok 音轨 403 拖死 sample | 本轮 4/4；采样改为优先完整变体、403 音轨可跳过 |
| 推荐流感假相册 | 已收紧判定（未提交） |
| B 站媒体污染 xmfyy | 已加跨站过滤（未提交） |

---

## 5. 建议下一刀（优先级）

1. **抖音音轨配对（P0）**  
   - 保证 `__vdProbe` 对当前 `aweme_id` 必 visit `music` / `play_url`  
   - Queue 时强制 `ContentIdentity` 与页主 identity 一致  
   - Overlay 浏览器视频时优先配对非 FLV audio  

2. **xmfyy / MacCMS 通用 HLS（P0）**  
   - 确认子 frame Network 事件进 `ProcessAsync`（打日志：host=`qlplayer` / `.m3u8`）  
   - 必要时对 parse iframe `Runtime.evaluate` 抽 `hls.url` / `video.src`  
   - 验收：identity 用 `/id/290850/`；caption 用 `vod_name` / h2「肖申卓」  

3. **提交未推送改动（P1）**  
   - 见 §6；避免交接人在脏树上继续改  

4. **回归命令**  
   ```powershell
   scripts/publish-beta.ps1 -SkipVerify
   # 停掉 VideoDownloader / Verify
   scripts/verify-requested-sites.ps1 -SkipBuild:$false
   ```

---

## 6. 工作区状态（交接时）

**已推送：** `origin/clean-main` @ `a4b0647`

**未提交（请先 `git diff` 再决定提交）：**

- `VideoObservationScript.cs` — playinfo / MacCMS / 相册阈值
- `MediaAddressDiscoveryScript.cs` — player_aaaa / iframe
- `UnifiedMediaPipeline.cs` — 跨站泄漏、相册最少张数、image CDN、音轨配对排序等
- `MediaOwnership.cs` — `/note/`、`/id/<n>/`
- `MediaVariantRanking.cs` — 相册排序权重
- `LiveAcceptance.cs` — 采样/文案/切换/弹窗等待/HLS fallback
- `tests/verify-address-discovery.cjs` — 脚本 hash

**不要提交：** `.tmp-tools-btbn-3/`、bin/obj、publish 垃圾。

---

## 7. 架构约束（勿破坏）

- 仍走 **统一** `UnifiedMediaPipeline` + `DownloadEngine`；不要为单站再开下载管线  
- 站点差异放在 `ISiteMediaAdapter` / 观察脚本，不把 Cookie 当默认修复  
- 相册是协议/下载后端能力（`FfmpegAlbumSlideshow`），Douyin 只负责发现  

---

## 8. 关键文档

- `DOCS/PROBE_FLOW_20260908.md` — 探测流  
- `DOCS/SITE_ADAPTER_PHASE1_20260908.md` — 适配器 Phase1  
- `DOCS/LIVE_ACCEPTANCE_20260908.md` — 更早一轮报告（含 YouTube；URL 集已变）  

---

*交接写于 2026-09-08；以 `artifacts/live-acceptance/runs/20260908-162935-a6d2cdc7` 与工作区未提交 diff 为准。*
