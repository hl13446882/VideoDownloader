# 探测器方法成功率（按探测器独立统计）

**硬规则：成功率只在同一探测器内部比较与排序。**
抖音 / B站 / YouTube / TikTok 的方法账本互不混用（例如抖音没有 `ytdlp`，也不会用 YouTube 的 `ytdlp` 成功率）。

- 最近一轮：`（尚未跑过；文档结构已按探测器分区）`
- 更新时间：—
- 机器账本：`%LOCALAPPDATA%\VideoDownloader\probe-method-stats.json`（键 = detectorId + method）

## YouTube 探测器（`youtube`）

本探测器内方法成功率（仅与本探测器其它方法比较）：

| 排序 | 方法 | 成功 | 尝试 | 成功率 |
|---:|---|---:|---:|---:|
| #1 | `ytdlp` | 0 | 0 | — |
| #2 | `network_cdn` | 0 | 0 | — |
| #3 | `browser_play` | 0 | 0 | — |
| #4 | `dom_observation` | 0 | 0 | — |
| #5 | `ytdlp.client:android,web` | 0 | 0 | — |
| #6 | `ytdlp.client:ios,web` | 0 | 0 | — |
| #7 | `ytdlp.client:web` | 0 | 0 | — |
| #8 | `ytdlp.client:tv_embedded` | 0 | 0 | — |
| #9 | `ytdlp.client:default` | 0 | 0 | — |

本轮该探测器明细：

| 地址 | 获胜方法 | 通过 | 取址ms | 无效探测 |
|---|---|---|---:|---|
| — | — | — | — | 本轮无该探测器样本 |

## B站 探测器（`bilibili`）

本探测器内方法成功率（仅与本探测器其它方法比较）：

| 排序 | 方法 | 成功 | 尝试 | 成功率 |
|---:|---|---:|---:|---:|
| #1 | `ytdlp` | 0 | 0 | — |
| #2 | `network_cdn` | 0 | 0 | — |
| #3 | `browser_play` | 0 | 0 | — |
| #4 | `dom_observation` | 0 | 0 | — |

本轮该探测器明细：

| 地址 | 获胜方法 | 通过 | 取址ms | 无效探测 |
|---|---|---|---:|---|
| — | — | — | — | 本轮无该探测器样本 |

## 抖音 探测器（`douyin`）

本探测器内方法成功率（仅与本探测器其它方法比较）：

| 排序 | 方法 | 成功 | 尝试 | 成功率 |
|---:|---|---:|---:|---:|
| #1 | `network_cdn` | 0 | 0 | — |
| #2 | `browser_play` | 0 | 0 | — |
| #3 | `dom_observation` | 0 | 0 | — |
| #4 | `album_images` | 0 | 0 | — |

本轮该探测器明细：

| 地址 | 获胜方法 | 通过 | 取址ms | 无效探测 |
|---|---|---|---:|---|
| — | — | — | — | 本轮无该探测器样本 |

## TikTok 探测器（`tiktok`）

本探测器内方法成功率（仅与本探测器其它方法比较）：

| 排序 | 方法 | 成功 | 尝试 | 成功率 |
|---:|---|---:|---:|---:|
| #1 | `ytdlp` | 0 | 0 | — |
| #2 | `network_cdn` | 0 | 0 | — |
| #3 | `browser_play` | 0 | 0 | — |
| #4 | `dom_observation` | 0 | 0 | — |
| #5 | `album_images` | 0 | 0 | — |
| #6 | `ytdlp.url:embed` | 0 | 0 | — |
| #7 | `ytdlp.url:canonical` | 0 | 0 | — |

本轮该探测器明细：

| 地址 | 获胜方法 | 通过 | 取址ms | 无效探测 |
|---|---|---|---:|---|
| — | — | — | — | 本轮无该探测器样本 |

## 方法含义（按探测器适用）

| 方法 | YouTube | B站 | 抖音 | TikTok | 含义 |
|---|---|---|---|---|---|
| `ytdlp` | ✓ | ✓ | ✗ | ✓ | 外部 yt-dlp |
| `network_cdn` | ✓ | ✓ | ✓ | ✓ | 网络 CDN / progressive |
| `browser_play` | ✓ | ✓ | ✓ | ✓ | 浏览器已播放 |
| `dom_observation` | ✓ | ✓ | ✓ | ✓ | DOM / playAddr |
| `album_images` | ✗ | ✗ | ✓ | ✓ | 图集图片 |
| `ytdlp.client:*` | ✓ | ✗ | ✗ | ✗ | YouTube player_client |
| `ytdlp.url:*` | ✗ | ✗ | ✗ | ✓ | TikTok embed / canonical URL |

历史轮次 JSON：`DOCS/probe-method-stats-rounds/<runId>.json`（内含 `byDetector` 分组）
