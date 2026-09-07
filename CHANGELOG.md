# Changelog

## 0.2.0-beta - 2026-09-02

- Added v1.2 site adapter structure for YouTube, Bilibili, Douyin, TikTok, and Generic fallback.
- Added multi-track `MediaVariant` support for separated video/audio streams.
- Added bounded network event processing with preservation for high-value media and manifest events.
- Added TestMediaServer fixtures for redirect media, event flood pressure, disk-pressure helper streams, and HLS encryption/DRM boundary checks.
- Hardened FFmpeg invocation so Cookie, Authorization, and Proxy-Authorization are not passed through process arguments.

## 0.1.0-beta - 2026-09-02

- Initial WPF/WebView2 desktop application.
- Added Core, Infrastructure, and UI project split.
- Added MP4/HLS/DASH detection, download state machine, SQLite persistence, FFmpeg adapter, and local TestMediaServer.
