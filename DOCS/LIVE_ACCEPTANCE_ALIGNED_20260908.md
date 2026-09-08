# Live Acceptance + App/Verify Alignment — 20260908-083345

**runId:** `20260908-083345-af78955c`  
**result:** 14/14 records, **11 PASS / 3 FAIL** (previous run 10/14)

## Consistency work completed

| Change | App | Verify |
| --- | --- | --- |
| `AppServiceComposition` shared DI + `UserSettingsStore.Load` | yes | yes |
| Same WebView2 user-data folder (non-`--local`) | yes | yes |
| Sample via `MediaAvailabilityValidator` (manual redirect, no auto-cookie) | probe/download | live sample |
| `MediaVariantRanking` (demote FLV, prefer video size) | FocusLargest | live pick |
| FLV dropped when non-FLV video exists | reconciler | same pipeline |
| Extra DOM probe before `CompleteDiscovery` | RunPagePassAsync | same VM path |
| Keep probing when `AudioOnly`/`VideoDenied` | grace loop | same VM path |

## Per-site

| Site | Pass | Notes |
| --- | --- | --- |
| YouTube ×2 | 2/2 | Validator samples OK |
| Douyin recommend ×4 | 4/4 | Same-URL identity refresh; FLV no longer preferred |
| Douyin jingxuan | 1/1 | OK |
| TikTok ×4 | 1/4 | #1 Combined from browser despite yt-dlp `AUDIO_ONLY`; #2 audio-only 403; #3/#4 no formats / empty |
| Bilibili ×3 | 3/3 | OK |

## Remaining TikTok gap

CDN `HTTP_403` on video tracks after bounded recovery; browser sometimes supplies Combined (#1) but not always. Not fixed by enabling Cookie. Needs further DOM/network playAddr binding on feed swipe.

Caption acquisition was not modified.
