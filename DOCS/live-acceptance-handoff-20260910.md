# Live acceptance handoff (2026-09-10)

## User judgment (authoritative)
- **YouTube：已判定通过**（用户口头确认，2026-09-10）。
- **抖音：本战役不测 / 不改**。
- **后续验收：忽略 YouTube + 抖音**。

## Active run policy
- `scripts/verify-requested-sites.ps1` URL set excludes YouTube and Douyin.
- Sites under test: TikTok, Bilibili×3, xmfyy AES, ally multi-video.
- Pass bar: download proof **>20 MiB**; TikTok feed ≥6 scrolls / ≥5 downloads pass.

## Code baseline
- Commit deployed: `41802cf` → `D:\VideoDownload`
