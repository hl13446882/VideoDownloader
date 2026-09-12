# 下载性能与进度更新检查（2026-09-12）

## 结论

不能把所有站点的慢都归为线程数不足。YouTube 已确认存在请求块过大、并行跳转处理错误、进度上报过稀的问题；B 站当前样本更需要连接/CDN 实测选择，盲目增加并发没有显示稳定收益。HLS 已启用分片并发，FFmpeg 的普通音视频合并使用流复制。

## 日志证据

检查文件：`%LOCALAPPDATA%\VideoDownloader\logs\app20260912.log`。

- 13:54:58，YouTube 视频与音频各启用 8 路 Range 下载。
- 同时出现 HTTP 302 与 206。旧并行路径没有处理 302，视为 RANGE_MISMATCH；单连接路径则有跳转处理。
- 13:56:43 音频回退单连接，13:56:44 从 offset=0 下载 69,792,591 字节。
- 14:00:58 视频回退单连接，14:00:59 跳转到另一 CDN 后，从 offset=0 下载 214,389,683 字节。
- 并行的 Task.WhenAll 等待其他连接结束才暴露失败，加上回退清理分块，造成等待和重复传输。
- 11:11:51 另一 YouTube 任务报 SocketException 10053，外层归类为 FILE_IO；实际堆栈在 SSL/HTTP 读取，属于传输中断，并非磁盘读写性能的证据。

## 小流量实测

curl 跟随跳转，接收正文后丢弃，不修改客户端任务或下载文件。每个请求最多 12–15 秒；超时样本的速度是实际收到字节数除以总耗时。测量期间客户端仍下载，结果含网络竞争和时间波动，不能当作独占链路带宽或未来速度保证。

### YouTube：同一签名地址、同一 CDN IP

CDN：`rr1---sn-ojnpo5-5j.googlevideo.com`，IP `173.194.146.97`。

| 请求范围大小 | 实收 | 平均速度 | 结果 |
|---|---:|---:|---|
| 32 MiB | 48 KiB | 3.2 KiB/s | 15 秒截止 |
| 1 MiB | 1 MiB | 422.1 KiB/s | 完整，2.43 秒 |
| 8 MiB | 约 2.95 MiB | 201.4 KiB/s | 15 秒截止 |
| 1 MiB（重复） | 1 MiB | 479.8 KiB/s | 完整，2.13 秒 |

这些是 curl 请求级结果，尚不是新版客户端完整文件的对照测试。YouTube 探针未复用浏览器身份头；比较中的请求头保持一致。

yt-dlp 官方 FAQ 明确提示 YouTube 对超过 10 MB 的 HTTP chunk 请求限速：
https://github.com/yt-dlp/yt-dlp/wiki/FAQ#i-extracted-a-video-url-but-it-does-not-play-on-another-machine--in-my-web-browser

原实现将文件平均分成 8 块：214 MB 视频每块约 26.8 MB，717 MB 视频每块约 89.7 MB。并发连接数与每次 HTTP 请求大小是两个独立参数。

原始指标：`artifacts/youtube-speed-results.json`。

### B 站：当前 COS 与重新解析出的同一对象 Akamai

探针复用任务/解析器的 User-Agent、Referer、Origin 等请求头。候选 CDN 用文件路径末尾的对象名做匹配，没有手工改写签名 URL。尚未逐字节验证跨 CDN 内容，不会据此直接拼接客户端已有文件。

| 路径 | 平均速度 | 说明 |
|---|---:|---|
| 客户端正在使用的 COS 单连接 | 约 171 KiB/s | 下载临时文件 12 秒增量估计，受文件缓冲影响 |
| COS 新建连接，32 MiB 请求 | 590.1 KiB/s | 12 秒内收到约 6.92 MiB |
| COS 新建连接，1 MiB 请求 | 257.5 KiB/s | 完整 |
| COS，4 路各 1 MiB | 总体约 493 KiB/s | 4 MiB / 8.31 秒 |
| COS 新建连接，8 MiB 请求 | 371.8 KiB/s | 12 秒内收到约 4.36 MiB |
| COS，4 路各 8 MiB | 总体约 412 KiB/s | 截止时实际收到约 4.91 MiB |
| Akamai，同一对象 8 MiB 请求 | 695.5 KiB/s | 完整，11.78 秒 |

当前任务是单轨 DirectHttp，源地址为 `upos-sz-mirrorcosov.bilivideo.com`，未保存 Alternatives。重新解析可得到 `upos-hz-mirrorakam.akamaized.net` 候选。固定 CDN 域名评分不足以表示当前用户线路速度；本次单样本也不足以将 Akamai 永久设为最高优先级。

原始指标：`artifacts/bilibili-speed-results.json`、`artifacts/bilibili-speed-large-results.json`、`artifacts/bilibili-alternate-speed-results.json`。

## 各下载器检查

| 路径 | 当前机制 | 发现与处理 |
|---|---|---|
| YouTube DirectHttp / 多轨 HTTP | 8 路范围请求，音视频同时下载 | 已改为每个 HTTP 请求不超过 1 MiB；保留已有磁盘分块布局 |
| B 站 DirectHttp / 多轨 HTTP | 单轨单连接；多轨可同时下载；断线 Range 重试 | 没有证据支持直接套用 YouTube 的小块/高并发参数；保留当前兼容逻辑，补充真实速度日志 |
| 抖音、TikTok 渐进式/图集素材 | HTTP；部分 CDN 首次请求不支持 Range | 保留站点现有 Range 特例，避免加速改动引入 403 或内容损坏 |
| HLS/DASH manifest | N_m3u8DL-RE；thread-count=6，concurrent-download，retry=3 | 未传入 max-speed；已有分片并行与缓存。增加线程需按站点对照验证 |
| FFmpeg 音视频合并 | -c copy，本地素材合并 | 常规合并不重新编码；不是本次网络阶段慢的原因 |
| FFmpeg 图集视频 | 图片与音频下载后编码 | 编码速度和网络下载速度应分别观察 |
| UI 进度 | DispatcherTimer 每 400 ms 读取任务内存 | 并行 HTTP 原来每连接累计 2 MiB 才推送，造成几十秒空窗；已改为收到数据时约每 250 ms 上报 |
| 外部进程进度 | 每 300 ms 扫描临时文件；2 分钟无变化超时 | 大量 HLS 分片时可能增加磁盘扫描成本，需要大任务性能记录后再替换为结构化进度 |

## 已落地修改

1. 并行 Range 跟随 CDN 跳转，保留范围与按目标域构建的请求上下文（上一轮修复）。
2. YouTube 每次请求最多 1 MiB，包含已有连续 `.part` 的单连接续传；不改变分块文件编号与边界。
3. 并行进度按时间发布；并行计数和音视频汇总串行发布，减少过时计数覆盖新计数。
4. 单个分块传输中断或服务器临时错误，在预算内从已经写入的字节位置重试。
5. 不可恢复的并行分块错误取消同组其他请求，避免等完整大块下载完才回退。
6. HTTP 活跃传输每约 10 秒输出实际字节增量与 KiB/s，区分单连接与并行。无数据时不输出心跳；短于 10 秒的请求未必产生该速度日志。

## 后续优化优先级

- B 站：在专属检测器保留同对象备用地址；用少量数据测候选速度和稳定性，选择实际较好的 CDN。跨 CDN 续传前验证对象、质量、总长度及必要的字节一致性。
- 通用 HTTP：区分“无数据超时”和“持续低速”。当前 HttpClient 头部超时不能覆盖 ResponseHeadersRead 之后的长期正文停滞；应增加读取空闲超时和有限续传，避免无限挂起。
- B 站低速恢复：连续低速窗口后，有限次数尝试新连接/重新解析同对象。不要因一次短时抖动清零进度或不断切换。
- HLS：用同一清单比较 6、8、12 路，统计 403/429/重试与完整任务耗时后选择。外部进程输出若增加结构化字节进度，可替代递归文件扫描。
- 多任务：保留总并发预算，避免“3 个任务 × 双轨 × 每轨 8 路”同时争抢连接。更高并发不保证更高吞吐。

## 验证与交付

- HTTP 下载、下载引擎相关筛选项及进程进度测试合计 27 项通过。
- 测试工程的 `ExclusiveSiteDetectionTests.cs` 有既有构造参数缺失编译错误。本轮用 `artifacts/youtube-test-isolation.targets` 临时排除该文件后运行相关测试；不是全量测试通过。
- 新增测试覆盖：1 MiB 请求上限、跳转后分块拼接的逐字节一致性、慢读时提前上报、断流只重试缺失字节、旧连续 `.part` 续传。
- Release 单文件发布成功：`artifacts/download-speed-fix/VideoDownloader.exe`。需关闭客户端后替换安装目录中的主程序；此文件依赖原安装目录的 tools、ffmpeg、M3u8 等工具目录。
- 尚未在新版运行进程上完成全文件下载速度对照，因此没有承诺固定倍数的端到端加速。

未修改运行中的客户端进程、任务数据库、当前下载文件，也未将未经充分验证的 B 站 CDN 规则或并发默认值投入使用。
