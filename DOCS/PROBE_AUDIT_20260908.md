# 探测能力审计（2026-09-08 矩阵 Y1–C1）

## 范围

代码级行为审计。不修改文案获取/优先级/文件命名；不以“开启 Cookie”为默认修复。

## 缺陷与修复对照

| 编号 | 原缺陷 | 代码修复 | 自动化证据 |
| --- | --- | --- | --- |
| Y1 | YouTube 人机验证后仍轮询多客户端 | `YtDlpResolver` 分类 `IsHumanVerificationError`，同上下文立即停轮询；不再追加 Cookie/登录归因 | `Y1_*` |
| Y2 | 外置失败缺少丢弃原因 | `ProbeCandidateDecision` + `LastProbeDecisions`（来源/类型/归属/结果/原因/主机） | 各用例断言 decision |
| Y3 | 首页触发外置解析 | `ShouldRunExternalResolve`：无稳定地址且无 `content:` 身份则跳过 | `Y3_*` |
| T1 | 视频 403 直接剔除 | 探测期 `ProbeSampleGate`：同批备用 → 有界重新解析 → 再抽样；保留音轨 | `T1_*` |
| T2 | 仅音频当完整成功 | `MediaAvailabilityKind` + `StatusHint`/`Metadata.videoFailure`；`LastExternalError` 保留 `AUDIO_ONLY`/`HTTP_403` | `T2_*` |
| T3 | Cookie 关闭仍可能携带 | 抽取 JSON 不提升 Cookie 头；`CaptureCookies=false` 清空 Cookies；`RequestMessageFactory` 双开关才发送 | `Headers_*` + 代码审查 |
| C1 | 恢复与切页并发污染 | 恢复/校验绑定 `SessionId`+`SamePage`；Clear 后旧结果不回写 | `C1_*` / FrameDiscovery |

## 残留风险（实机验收）

1. YouTube 真机风控是否解除：代码仅停止无效轮询并保留 `HUMAN_VERIFICATION`，不保证可解析。
2. TikTok CDN 403 根因：已保真 Referer/Origin/签名 URL、禁用 Cookie 时不发送；若 CDN 仍拒，需对照实机下载请求。
3. HLS/DASH 仍走原有清单校验，不经二进制小样本。
4. 无稳定身份的历史任务无法安全恢复。

## 建议实机步骤

1. YouTube 触发人机验证页：确认不再连打五客户端，状态含 `HUMAN_VERIFICATION`。
2. TikTok 视频 403、备用可用：探测结果含视频+音轨。
3. TikTok 视频全 403、音频可用：显示仅音轨/部分成功，不宣称完整视频可下。
4. YouTube/TikTok 首页：不启动外置解析；进入具体视频后再解析一次。
5. 探测 A 恢复中切到 B：结果仅为 B。
