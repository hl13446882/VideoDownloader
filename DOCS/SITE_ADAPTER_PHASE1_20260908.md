# SiteAdapter Phase 1（2026-09-08）

## 审查摘要

| 散落位置 | 站点特判 | Phase 处置 |
|----------|----------|------------|
| `UnifiedMediaPipeline.IsCandidate` path/query hints | videoplayback/playAddr 等 | 通用形态仍留 Generic；TikTok CDN 强认收入 TikTokAdapter |
| `ResolveExternalPageUrl` | TikTok/Douyin feed 改写 | TikTok 改写迁入 `TikTokMediaAdapter.CanonicalizeExternalPageUrl`；Douyin 暂留静态 fallback |
| SABR 过滤 | YouTube | 暂放入 Generic（Phase 2 再迁 YouTubeAdapter） |
| `BrowserObserved` / Overlay / ProbeSampleGate | TikTok 动机 | **机制保持通用**；TikTok `ValidationPolicy` / `StrongAccept` 强化 |
| `MediaAddressRenewal` / `YtDlpResolver` | 多站 | Phase 2+ |
| 休眠的旧 `ISiteAdapter.ProbeAsync` | 整页 Probe | **不接入本轮**；与 live `UnifiedMediaPipeline` 并存 |

## Phase 1 落地

```
ISiteMediaAdapter + Resolver + CandidateDecisionPolicy
├─ GenericMediaAdapter（fallback）
└─ TikTokMediaAdapter（StrongAccept CDP 实播）
        ↓
UnifiedMediaPipeline.ProcessCoreAsync 组合决策
        ↓
既有 Queue → Inspect → Publish
```

坚持：Adapter 只负责发现/识别/刷新提示；不复制 Pipeline / Downloader。
