# 抖音下载故障调查（2026-09-09）

## 1. 分段下载失败与完整性误判

- 运行客户端：`D:\VideoDownloader\publish\VideoDownload\VideoDownloader.exe`。
- 日志：`%LOCALAPPDATA%\VideoDownloader\logs\app20260909.log`。
- 任务：`842bcab3-b9b8-44ce-a4f5-3fc3af9f7a66`，0909《思念妈妈》人声示范动态谱。
- 13:49:08 专属 DouyinMediaDetector 选中 Combined，实体长度 19,969,542。
- 13:49:46 首次 HTTP 206 后留下 204,801 字节，报 INCOMPLETE_DOWNLOAD。
- 13:49:56 从 204,801 续传；13:50:21 再报 INCOMPLETE_DOWNLOAD。
- 13:50:31 从 19,969,542 请求，返回 416，经 IsAlreadyComplete 分支完成。
- 数据库最终 downloaded_bytes=total_bytes=19,969,542，无错误。ffprobe 识别 H.264 1920×1080 + AAC，213.133991 秒；尚未全片解码。

代码问题：

1. HttpMediaDownloader.EnsureDownloadLooksComplete 对首个短 206 窗口抛错，依赖队列延迟重试，没有在当前传输中连续取剩余区间。
2. HttpMediaDownloader 写入后累加 job.DownloadedBytes；DownloadEngine 的 Progress<long> 异步回调又赋值同一字段。存在旧进度覆盖新计数的竞争；完整性检查依赖该字段。与第二次失败高度吻合，但未记录当时实际计数，因果仍待回归复现。
3. RequestMessageFactory 未排除 Range/If-Range；零偏移下载未主动规范 Range。本任务持久化请求上下文 Range 为 bytes=0-，不能断言当时为固定的 200KB 区间。

建议：下载字节计数单一写入者；按实际文件长度和响应实体范围校验；下载器管理 Range；连续处理部分响应，并记录响应 Content-Range、实体长度、实际写入量。保留四站独占探测边界。

## 2. “别让我一个人醉”显示完成但无法播放

- 任务：`84144f2e-5e71-4ea5-aba2-48ea9ff37c47`。
- 数据库任务原名带 #；实际检查文件为 `C:\Users\lzd11\Downloads\别让我一个人醉.mp4`。
- 数据库状态完成，downloaded_bytes=total_bytes=38,764,099，无错误；实际文件长度同值。
- ffprobe 报 `moov atom not found`、`Invalid data found when processing input`，无法识别音视频轨道。
- 本地全文件未找到 ftyp/moov/mdat/moof/styp 标记，开头为媒体数据而非 MP4 文件头。
- 持久化请求上下文包含 `Range: bytes=819201-922541`。RequestMessageFactory 原样透传 Range；DownloadDirectCoreAsync 仅在 offset>0 时覆盖 Range，HandleResponseAsync 也仅在 offset>0 时核验 206 的 Content-Range 起点。因此首次请求存在从非零远端位置写入本地零位置的明确漏洞。
- 13:53:39 首次 offset=0，返回 206 后报 INCOMPLETE_DOWNLOAD；13:53:50 按本地长度 815,758 续传；13:54:50 数据库记录完成。
- 对相同源 URL 独立请求 bytes=0-8191：HTTP 206、Content-Range=bytes 0-8191/38764099，ftyp 位于偏移 4、moov 位于偏移 36，证明源资源具有正常 MP4 文件头，本地缺失。
- 独立请求源偏移 815,758 的 64 字节，与本地相同位置的 64 字节完全一致，支持后续续传区间正确、首次写入区间损坏的判断。
- 源偏移 819,201 的 64 字节不匹配本地开头，因此不能将最终持久化 Range 直接认定为首次请求实际返回的区间；原日志没有记录响应 Content-Range，确切首次远端偏移尚未还原。
- 结论：当前文件虽达到实体总长度，但头部内容错误，moov 缺失导致播放器无法解析。首次 Range 透传与缺少零偏移校验是高度吻合的代码原因；仅检查大小及最终将实际大小写回 TotalBytes 无法保证容器有效。
- 修复方向：新任务明确从字节 0 请求；所有 206 均校验 Content-Range.From 与写入偏移一致；最终验证容器；对已损坏文件重新获取正确前缀或重新下载，并独立校验后再交付。未改动本地原文件。

以上为修复前调查记录。

## 3. 统一修复与验证

- RequestMessageFactory 不再透传浏览器 Range/If-Range。HTTP 下载器按实际本地偏移设置开放区间，并请求 identity 编码。
- 所有 206（包括 offset=0）校验起点、终点与实体长度；按响应范围校验实际写入量，短窗口连续续传，不再等待队列重试。
- 重定向次数与分段次数分开计算；实体版本/长度变化会重置部分文件。416 仅在本地长度严格等于远端实体长度时允许进入完成校验。
- 删除直接下载的异步计数回写；多轨进度改为同步报告，避免结束后旧进度覆盖总数。下载循环使用自身计数与实际文件长度进行完整性判断。
- MP4/M4A/MOV 完成前检查顶层 box 边界、moov 和 mdat；无效部分文件重置，避免等长坏文件通过 416 路径变成“已完成”。这是容器结构检查，不是每次全片解码，也不保证所有编码均被系统播放器支持。
- 日志新增响应状态、请求偏移、Content-Range、Content-Length 和脱敏错误详情。
- 相关回归测试 51 项通过，包含 6 项新测试：浏览器范围污染与超过 8 个窗口、首次错位 206、等长坏文件 + 416、短响应体、服务器忽略续传、旧进度覆盖。
- 扩大基础设施测试：新增最后一项测试前，206 通过、6 失败、1 跳过。失败的 6 项在独立 HEAD=3bd0717 基线中全部复现（RepairMatrixTests 的 5 项旧共享探测测试，以及 UnifiedDownloadIntegrationTests），不是本次引入；未修改这些不相关流程。
- 用修复后的生产 HttpMediaDownloader 重新获取“别让我一个人醉”：38,764,099 字节；ffprobe 识别 H.264 720×1280 + AAC，111.479002 秒；ffmpeg -xerror 全片解码成功。
- 原损坏视频备份在 artifacts/douyin-repair/original-corrupt.mp4；验证后的新文件替换用户 Downloads 中同名文件。客户端关闭后，备份任务数据库并纠正该任务的目标路径（原记录残留 #），与实际文件一致。
