# Download and Discovery Repairs: 2026-09-08

## Scope

Code-only verification. No real-site access, UI launch, publishing, or changes to running clients.
Caption extraction and filename policy are unchanged. Their original file hashes are asserted by the Node verification script.

## Implemented

| Area | Repair | Code verification |
| --- | --- | --- |
| External direct media | Bounded GET sample with downloader request factory, redirects, HTTP status and container checks; reject 403/HTML/empty responses | HTTP fixtures and production pipeline test |
| Recovery | Persist content identity, fixed recovery URL and up to four same-quality alternatives; validate backups before fixed-page re-resolution; reject different content | Renewal and persistence tests |
| Request context | Preserve typed UA/Referer/Origin with raw fallback; honor disabled cookie capture; filter domain/path/secure/expiry; do not refresh old jobs from a different active page | Request factory regression tests and code inspection |
| Article discovery | Separate address-only DOM/JSON script without stable-ID requirement; same-origin frames and bounded CDP isolated-world frame sampling | Executed JavaScript fixtures and mocked CDP tests |
| Stale frames | Reject old session results and replaced frame loaders | Frame/session tests |
| Response configuration | Bounded JSON plus literal media-key URL extraction; no arbitrary script evaluation; unstructured extraction excluded from identified feeds | Scanner test |
| Diagnostics | Sample rejection host/status, candidate/usable counts, frame/response counts; retain original HTTP_403 when recovery fails | Code inspection and error-code tests |

## Verification Commands

```powershell
dotnet test tests/VideoDownloader.Core.Tests --no-restore
dotnet test tests/VideoDownloader.Infrastructure.Tests --no-restore
node tests/verify-address-discovery.cjs
dotnet build src/VideoDownloader.UI --no-restore
```

TRX results: `artifacts/code-verification-track-repairs/repair-matrix*.trx`.

## Boundaries and User Acceptance

- HLS/DASH retain existing manifest/backend validation; the new binary sampler does not certify their segments.
- Cross-origin CDP behavior is tested with protocol fixtures, not a live WebView2 instance.
- No universal discovery guarantee: encrypted/DRM media, runtime-only obfuscated player configuration and unavailable browser frame contexts remain outside this patch's proof.
- Old saved jobs without a known stable identity/permalink cannot safely recover a historical feed video; rediscovery is required.
- TikTok feed permalink fallback uses the existing `/@i/video/{id}` convention; actual site acceptance remains for the user.
- Real acceptance: retry the failing TikTok item, then the two article URLs from the logs; confirm byte progress with a partial download and unchanged captions. Site/CDN rejection can still occur and must be distinguished from code-level test success.
- The `publish` directory is unchanged. Existing deployed clients do not include these source changes.
