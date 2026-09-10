# 探测器方法成功率（按探测器独立统计 · 真正取址方法）

**硬规则：成功率只在同一探测器内部比较与排序。**
只统计真正取址方法，不把 DOM/browser 等“看见媒体”的弱信号与主方法平级。

- 最近一轮：`（方法账本已按 2026-09-10 契约收缩；等待下轮验收写入）`
- 机器账本：`%LOCALAPPDATA%\VideoDownloader\probe-method-stats.json`（键 = detectorId + method）

## YouTube 探测器（`youtube`）

| 排序 | 方法 | 角色 |
|---:|---|---|
| #1 | `ytdlp.pot_mweb` | **主** — mweb + PO Token Provider |
| #2 | `ytdlp.default` | 回退 — yt-dlp 默认 client |
| #3 | `ytdlp.web_embedded` | 回退 |
| #4 | `network_media` | 最后回退 |

> 已删除平级：`dom_observation` / `browser_play` / `android,web` / `ios,web` / `tv_embedded` 等。

## B站 探测器（`bilibili`）

| 排序 | 方法 | 角色 |
|---:|---|---|
| #1 | `playurl_api` | **主** — bvid/cid → wbi playurl → DASH（待直连实现） |
| #2 | `network_playurl` | 回退 — 浏览器捕获 playurl/upos |
| #3 | `ytdlp` | 回退 |

## 抖音 探测器（`douyin`）

| 排序 | 方法 | 角色 |
|---:|---|---|
| #1 | `aweme_detail` | **主** — 拦截 `aweme/v1/web/aweme/detail` JSON |
| #2 | `network_media` | 回退 — CDN / play_addr |
| #3 | `video_element` | 回退 — 当前 video 元素 |
| #4 | `router_data` | 回退 — `_ROUTER_DATA` 等 |
| #5 | `album` | 图集独立 |

## TikTok 探测器（`tiktok`）

| 排序 | 方法 | 角色 |
|---:|---|---|
| #1 | `ytdlp` | **主** — TikTok 专用 extractor |
| #2 | `web_data` | 回退 — SIGI_STATE / UNIVERSAL_DATA |
| #3 | `network_media` | 回退 — play_addr / CDN |
| #4 | `video_element` | 回退 |
| #5 | `album` | 图集独立 |

> `embed` / `canonical` 仅作 yt-dlp 内部 URL 回退，**不计入**平级成功率。

历史轮次：`DOCS/probe-method-stats-rounds/<runId>.json`
