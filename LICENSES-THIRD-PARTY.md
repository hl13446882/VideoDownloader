# Third-Party License Notes

This beta package depends on third-party components distributed under their own licenses. Verify the exact package versions from `VideoDownloader.UI.deps.json` in the published folder for each release candidate.

| Component | Purpose | Distribution note |
|---|---|---|
| Microsoft .NET Runtime / Windows Desktop Runtime | Application runtime | Framework-dependent package; installed separately by the user. |
| Microsoft Edge WebView2 Runtime / SDK | Embedded browser | Runtime installed separately by the user; SDK binaries are NuGet dependencies. |
| Microsoft.Extensions.* | Hosting, dependency injection, configuration, logging | NuGet dependencies. |
| CommunityToolkit.Mvvm | MVVM helpers | NuGet dependency. |
| Microsoft.Data.Sqlite / SQLitePCLRaw / e_sqlite3 | SQLite persistence | NuGet/native dependencies. |
| Dapper | SQLite data access | NuGet dependency. |
| Serilog / Serilog.Extensions.Logging / Serilog.Sinks.File | Structured file logging | NuGet dependencies. |
| FFmpeg | Optional remux backend | Not bundled by this package. Users configure their own `ffmpeg.exe` path or PATH entry. |
| yt-dlp | Optional external site resolver | Not bundled by this package. Users configure their own `yt-dlp.exe` path. |

Release candidates must include the full upstream license texts before being promoted from a test folder to a formal installer.
