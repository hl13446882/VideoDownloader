# Video Downloader

Windows 桌面「网页视频探测 + 下载器」，基于 .NET 10 / WPF / WebView2 / CDP。

## 架构

- `VideoDownloader.UI` — WPF / MVVM / WebView2 界面
- `VideoDownloader.Core` — 领域模型、检测、聚合、下载状态机
- `VideoDownloader.Infrastructure` — WebView2、HTTP、SQLite、日志
- `VideoDownloader.TestMediaServer` — 本地测试媒体服务（T01–T20）

## 前置条件

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- FFmpeg（HLS/DASH remux，需在设置中配置路径或加入 PATH）

## 快速开始

```powershell
# 1. 启动测试媒体服务器
dotnet run --project tools/VideoDownloader.TestMediaServer

# 2. 启动主程序（新终端）
dotnet run --project src/VideoDownloader.UI
```

默认地址栏指向 `http://localhost:5088/`。

## 功能概览

| Sprint | 能力 |
|--------|------|
| 1 | WebView2 浏览、CDP 双源探测、MP4 下载 |
| 2 | HLS/DASH 解析、FFmpeg remux、DRM 识别 |
| 3 | 断点续传（Range/ETag/416）、任务队列与启动恢复 |
| 4 | 403 上下文刷新、5xx 重试、设置页、日志脱敏、Beta 打包 |
| v1.2 | 站点适配（YouTube/Bilibili/抖音/TikTok + Generic）、多轨 MediaVariant、极小资源过滤 |

## 站点适配（v1.2）

各站点通过独立 `ISiteAdapter` 实现，由 `SiteAdapterRouter` 统一调度；通用网络探测作为 Generic 回退。

| 站点 | 探测方式 | 下载后端 |
|------|----------|----------|
| YouTube | googlevideo 网络 + 可选 yt-dlp | DirectHttp / FFmpeg 多轨 |
| Bilibili | WebView2 注入 `__playinfo__` + m4s 网络 | FFmpeg 多轨 remux |
| 抖音 / TikTok | 页面脚本 + 网络 | DirectHttp / FFmpeg |
| 其他 | Generic 网络探测 | 按 MIME/容器自动选择 |

发现列表自动忽略 **0 字节** 或 **小于 64KB** 且无强 MIME 的资源行。

可选外部解析器：`yt-dlp.exe`（YouTube/TikTok），非硬依赖，可在设置中配置路径。

## 设置

主界面「设置」按钮，或编辑 `%LOCALAPPDATA%\VideoDownloader\settings.json`：

- 保存目录、最大并发、重试次数
- FFmpeg 路径
- 启动时自动恢复已暂停下载
- 日志级别
- 站点适配开关与 yt-dlp 路径（`settings.json` 中 `sites` / `externalResolvers` 节点）

## 数据目录

- 浏览器数据：`%LOCALAPPDATA%\VideoDownloader\WebView2Data`
- 用户设置：`%LOCALAPPDATA%\VideoDownloader\settings.json`
- 下载数据库：`%LOCALAPPDATA%\VideoDownloader\data\downloads.db`
- 日志：`%LOCALAPPDATA%\VideoDownloader\logs\`

Cookie/Authorization 等敏感字段在 SQLite 中以 DPAPI 加密存储；日志输出经脱敏处理。

## 测试

```powershell
dotnet test
```

TestMediaServer 覆盖 T01–T20 场景（见规范第 15 章）。

## Beta 打包

```powershell
./scripts/publish-beta.ps1
```

输出目录：`publish/`（含 `VideoDownloader.UI.exe` 与 `TestMediaServer/`）

## 许可

Beta 版本仅供测试。第三方组件（WebView2、FFmpeg、Serilog 等）遵循各自许可证。

详细规范见 `DOCS/VideoDownloader_完整开发设计与验收规范.docx`。
