# SiteAdapter Phase 1（2026-09-08）

## 审查摘要

| 散落位置 | 站点特判 | Phase 处置 |
|----------|----------|------------|
| `UnifiedMediaPipeline.IsCandidate` path/query hints | videoplayback/playAddr 等 | 通用形态仍留 Generic；TikTok CDN 强认收入 TikTokAdapter |
| `ResolveExternalPageUrl` | TikTok/Douyin feed 改写 | 四站均经 `CanonicalizeExternalPageUrl`；静态 fallback 仅兜底 |
| SABR 过滤 | YouTube | **已迁入 YouTubeMediaAdapter** |
| `BrowserObserved` / Overlay / ProbeSampleGate | TikTok 动机 | **机制保持通用**；TikTok `ValidationPolicy` / `StrongAccept` 强化 |
| `MediaAddressRenewal` / `YtDlpResolver` | 多站 | Phase 2+ |
| 休眠的旧 `ISiteAdapter.ProbeAsync` | 整页 Probe | **不接入本轮**；与 live `UnifiedMediaPipeline` 并存 |

## Phase 2（四站独立探测）

四个网站各自注册独立 `ISiteMediaAdapter`，**站点有意见时不再走 Generic 形态学**：

| Adapter | 探测重点 |
|---------|----------|
| `TikTokMediaAdapter` | CDP 实播 StrongAccept、CDN playAddr、feed→`/@i/video/{id}` |
| `DouyinMediaAdapter` | playAddr/downloadAddr/aweme、modal_id、独立于 TikTok |
| `YouTubeMediaAdapter` | videoplayback/itag、**Reject sabr**、watch?v= / Shorts |
| `BilibiliMediaAdapter` | playurl/upos/m4s、BV/av identity |
| `GenericMediaAdapter` | 仅非上述站点的通用 fallback |

组合规则：`site.Kind != Default` → 采用 site；否则用 generic。
