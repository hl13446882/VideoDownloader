# Live acceptance handoff (2026-09-10)

## User judgment (authoritative)
- **YouTube：已判定通过**（跳过）。
- **抖音：本战役不测 / 不改**。
- **TikTok：live 验收通过**（20260910-090455-7ad234c0，5/6 ≥5，VERIFY_EXIT=0）。
- **策略**：发现问题立刻改；已修好的下轮快速过（有下载量即通过）。
- **共用 yt-dlp**：可改但必须窄、按站点分支，避免影响 YouTube/B站。

## TikTok fixes that landed
- Prefer durable CDN / demote webapp-prime; caption enrichment.
- yt-dlp via embed then `@tiktok/video/{id}` (canonicalize must not collapse `@user/video`).
- Reset `_failed` on content-id change; keep feed (no WebView embed nav for proof).
- Verify must rebuild after Infrastructure changes (`-SkipBuild` alone is unsafe).

## Remaining this campaign
- Bilibili ×3, xmfyy AES, ally multi-video.

## Baseline
- `7e40fa0` (+ TikTok latch `a3f718c`) → `D:\VideoDownload`
