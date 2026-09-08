# Live Acceptance Report — 2026-09-08

**runId:** `20260908-081324-bbcb7976`  
**evidence:** `artifacts/live-acceptance/runs/20260908-081324-bbcb7976`  
**result:** 14/14 records, **allPassed=false** (10 PASS / 4 FAIL)

## Requirement matrix

| # | Requirement | Evidence | Verdict |
| --- | --- | --- | --- |
| 1 | Same-URL feed refresh (TikTok / Douyin recommend) | Douyin identities `content:7678…` → `7681…` → `7671…` → `7681…`; TikTok `content:7681…` → `7653…` → `7681…` → `7682…`. ArrowDown advances; session identity updates without URL change. | **Pass** (identity + session refresh works) |
| 2 | No thrash probe without video change | All rows `stable=true`, settle-phase `switches≤1`. Same identity early-returns in `ObserveVideoIdentity` (1.5s debounce). | **Pass** |
| 3 | Broad A/V address discovery | YouTube 2/2, Bilibili 3/3, Douyin jingxuan 1/1, Douyin feed 3/4, TikTok 1/4 full A/V. Generic DOM/network path used when yt-dlp fails (Douyin cookie errors still yielded browser media). | **Partial** — TikTok video CDN 403 remains |
| 4 | Caption (do not change acquisition) | Caption ≠ `document.title` / host / bare filename on 13/14. TikTok #4 empty caption + empty variants. | **Pass** (no caption-code changes); one empty case |

## Per-site

| Site | Pass | Notes |
| --- | --- | --- |
| YouTube ×2 | 2/2 | Video+audio samples HTTP 206 |
| Douyin recommend ×4 | 3/4 | #1 FLV progressive (`video/x-flv`) rejected as non-VOD sample; #2–4 OK; same-URL identity switches OK |
| Douyin jingxuan | 1/1 | `modal_id` page OK |
| TikTok foryou ×4 | 1/4 | #1 Combined+Audio OK; #2/#3 `AUDIO_ONLY: HTTP_403` (video denied, audio kept — T2 semantics); #4 no usable media / caption |
| Bilibili ×3 | 3/3 | Video+audio OK |

## Failures (not Cookie defaults)

1. **Douyin #1:** playable Combined was FLV; acceptance correctly refuses FLV as fixed VOD sample. Live skip already ran once; residual FLV VOD-looking card.
2. **TikTok #2/#3:** external+sample path marks video `HTTP_403`, keeps audio (`AUDIO_ONLY`). Same-URL identity did refresh; video URL recovery still blocked by CDN.
3. **TikTok #4:** identity present, no validated variants, caption empty — probe completed empty.

## Tooling fix during this run

- Registered `LocalizationService` in `VideoDownloader.Verify` DI (startup was crashing before acceptance).

## Caption policy

No caption extraction / priority / naming code was modified in this pass.
