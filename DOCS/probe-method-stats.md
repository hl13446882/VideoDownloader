# 探测器方法成功率（probe-method-stats）

每轮 live 验收 / 探测结束后更新。运行时按成功率从高到低排列探测方法。

- 最近一轮：`（尚未跑过）`
- 更新时间：—
- 机器账本：`%LOCALAPPDATA%\VideoDownloader\probe-method-stats.json`

## 累计成功率（用于排序）

| 站点 | 方法 | 成功 | 尝试 | 成功率 | 排序建议 |
|---|---|---:|---:|---:|---|
| — | — | 0 | 0 | — | 尚无数据 |

## 本轮明细

| 站点 | 地址 | 获胜方法 | 通过 | 取址ms | 无效探测 |
|---|---|---|---|---:|---|
| — | — | — | — | — | 本轮无明细 |

## 方法说明

- `ytdlp`：外部 yt-dlp 解析
- `network_cdn`：网络拦截到的 CDN/progressive
- `browser_play`：浏览器已播放（CDP Media）
- `dom_observation`：页面 DOM / playAddr 观察
- `album_images`：图集图片收集
- `ytdlp.client:*`：YouTube player_client 变体
- `ytdlp.url:*`：TikTok 等解析 URL 变体（embed / canonical）
- `schedule:ytdlp_first` / `schedule:network_first`：外层调度策略（仅日志，不计入成功率）

历史轮次 JSON：`DOCS/probe-method-stats-rounds/<runId>.json`
