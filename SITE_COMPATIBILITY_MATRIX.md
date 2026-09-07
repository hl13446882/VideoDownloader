# Site Compatibility Matrix

Release candidate: `0.2.0-beta`

| Test | Site | Required result | Status | Date | URL / content id | Path | Notes |
|---|---|---|---|---|---|---|---|
| T21 / A5-01 | YouTube VOD | Detect downloadable variant; remux separated audio/video when needed | Not run | 2026-09-02 | TBD | Adapter or Generic | Requires current public non-DRM sample. |
| T22 / A5-02 | YouTube Shorts | Detect one Short without duplicate Adapter/Generic rows | Not run | 2026-09-02 | TBD | Adapter or Generic | Requires current public non-DRM sample. |
| T23 / A5-03 | Bilibili | Pair current-part DASH video/audio and download playable output | Not run | 2026-09-02 | TBD | Adapter or Generic | Requires current public non-DRM sample. |
| T24 / A5-04 | Douyin | Handle share redirect and short-lived URL without infinite 403 retry | Not run | 2026-09-02 | TBD | Adapter or Generic | Requires current public non-DRM sample. |
| T25 / A5-05 | TikTok | Detect and download; Generic fallback works if Adapter misses | Not run | 2026-09-02 | TBD | Adapter or Generic | Requires current public non-DRM sample. |
| T26 / A5-07 | Adapter disabled/failure | Generic continues and other adapters are isolated | Partially automated | 2026-09-02 | Local/unit fixtures | Generic fallback | Expand before release sign-off. |
| T27 / A5-07 | External resolver unavailable | Application and Generic continue to work | Partially automated | 2026-09-02 | Local/unit fixtures | Generic fallback | Expand before release sign-off. |
| T28 / A5-08 | Challenge or permission page | Do not bypass; ask user to complete access in WebView2 | Not run | 2026-09-02 | TBD | UI/Adapter | Manual RC check. |
| T29 / A5-09 | Site DRM | Return `DRM_PROTECTED`; disable download | Partially automated | 2026-09-02 | Local fixtures | Core policy | Needs site RC sample. |
| T30 / A5-10 | Short-lived signed URL | At most one re-probe; old context is not mutated | Not run | 2026-09-02 | TBD | Adapter | Needs dedicated integration test. |
