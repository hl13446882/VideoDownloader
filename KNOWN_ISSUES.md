# Known Issues

## 0.2.0-beta

- Formal installer packaging requires Inno Setup (`ISCC.exe`) on the release machine. `scripts/publish-beta.ps1` creates the framework-dependent test folder and builds the installer automatically when `ISCC.exe` is available.
- FFmpeg downloads intentionally do not receive Cookie, Authorization, or Proxy-Authorization headers through command-line arguments. Protected HLS/DASH that requires those headers may need a future local proxy or built-in segment downloader.
- Real public-site compatibility must be rechecked for each release candidate because YouTube, Bilibili, Douyin, and TikTok frequently change page and media delivery behavior.
- Automated WPF UI E2E coverage remains limited; release candidates still require manual WebView2 smoke testing on Windows 10/11 x64.
