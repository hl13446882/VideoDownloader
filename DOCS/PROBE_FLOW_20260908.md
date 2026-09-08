# 探测流程详解（至可下载音视频列表）

**日期：** 2026-09-08  
**范围：** 从会话触发到 UI 展示可下载视频/音频变体列表  
**主路径：** `MainViewModel` → `WebView2Host` → `UnifiedMediaPipeline`（+ `YtDlpResolver` / `ProbeSampleGate`）

本文包含总览图与三张拆解图，**每个菱形均为代码中的真实分支判断**。

---

## 0. 总览

```mermaid
flowchart TD
  T0([触发]) --> S[StartPageDetectionSession]
  S --> P[RunPagePassAsync]
  P --> D[DOM ProbeCurrentPage]
  P --> N[CDP 网络 / 响应体]
  P --> E[外置 yt-dlp 可选]
  D --> Q[Queue → Inspect]
  N --> Q
  E --> M[合并 _media + _external]
  Q --> M
  P --> C[CompleteDiscovery forceEmit]
  M --> C
  C --> U([UI：可下载列表])
```

| 阶段 | 关键类型 | 主要文件 |
|------|----------|----------|
| 会话 | `MainViewModel.StartPageDetectionSession` | `MainViewModel.cs` |
| 一轮探测 | `RunPagePassAsync` | `MainViewModel.cs` |
| DOM | `WebView2Host.ProbeCurrentPageAsync` | `WebView2Host.cs` |
| 网络 | CDP → `ProcessAsync` | `WebView2Host.cs` / `UnifiedMediaPipeline.cs` |
| 入队校验 | `Queue` / `InspectAsync` | `UnifiedMediaPipeline.cs` |
| 外置 | `TryExternalResolveAsync` + `ProbeSampleGate` | 同上 + `YtDlpResolver.cs` |
| 展示 | `PublishCore` / `CompleteDiscoveryAsync` | `UnifiedMediaPipeline.cs` |

---

## 1. 会话与 PagePass（入口分支）

```mermaid
flowchart TD
  T0([触发探测]) --> T1{触发类型?}
  T1 -->|NavigationStarted<br/>含同 URL 刷新| S1
  T1 -->|切标签 / PageIdentity| S1
  T1 -->|MediaSessionChanged| S1
  T1 -->|手动「探测」| SM[ResetDetectionPipeline<br/>_forceReplaceResults=true]

  S1[StartPageDetectionSession] --> S2{clearUi 且 !forceReplace<br/>且同 pageKey 正在探测?}
  S2 -->|是| S2N([忽略：防重复])
  S2 -->|否| S3[取消旧 CTS / 世代+1]
  SM --> S3
  S3 --> S4{clearUi?}
  S4 -->|是| S5[ClearDetectedVideos]
  S4 -->|否| S6[仅 ResetDetectionPipeline]
  S5 --> S7[RunStableDetectionSession]
  S6 --> S7

  S7 --> P0[RunPagePassAsync]
  P0 --> P1{generation == _pageGeneration<br/>且 Token 未取消?}
  P1 -->|否| P1N([中止本轮])
  P1 -->|是| P2{SelectedTab 已初始化?}
  P2 -->|否| P5[RequestContext.CreateEmpty]
  P2 -->|是| P3[RefreshContextSnapshotAsync]
  P3 --> P3b[ProbeCurrentPageAsync]
  P3b -->|异常| P4[status.contextWarn<br/>不中止整轮]
  P3b -->|成功| P5b[CaptureCurrentContext]
  P4 --> P5b
  P5 --> P6
  P5b --> P6{runExternal == true?}

  P6 -->|否| P7([DOM 已由 ProbeCurrentPage<br/>Submit 进管线])
  P6 -->|是| P8[ProbePageAsync<br/>runExternal=true]
  P7 --> P9
  P8 --> P9{runExternal?}
  P9 -->|是| P10[再次 ProbeCurrentPage<br/>补晚到 playAddr]
  P9 -->|否| P11
  P10 --> P11[CompleteDiscoveryAsync]
  P11 --> END([进入 Publish forceEmit])
```

### 分支说明

| 判断 | 真 | 假 |
|------|----|----|
| 同 `pageKey` 防抖 | 忽略本次启动 | 开新世代 |
| `clearUi` | 清空已发现列表 | 只清管线、保留 UI 直至更强结果 |
| Tab 未初始化 | 空上下文继续 | 刷新 Cookie/头 + DOM |
| DOM 失败 | 告警 | 正常捕获上下文 |
| `runExternal` | 跑 yt-dlp + 二次 DOM | 仅依赖已 Submit 的 DOM/网络 |
| `CompleteDiscovery` | 等会话空闲后 `Phase=Completed` 并 `forceEmit` | — |

---

## 2. 网络入队拆解（CDP → Queue → Inspect）

```mermaid
flowchart TD
  subgraph CDP[WebView2Host CDP]
    N0[Network.responseReceived<br/>/ loadingFinished] --> N1{粗筛像媒体?<br/>IsCandidate 或归属页}
    N1 -->|否| N1N([不入队])
    N1 -->|是| N2[EnqueueRawEvent]
    N2 --> N3[归一化为 NormalizedNetworkEvent]
    N3 --> PC[ProcessAsync / ProcessCoreAsync]
  end

  subgraph BODY[响应体扫地址]
    B0[可取 body 的文本响应] --> B1[扫出 URL]
    B1 --> B2{candidates 数量 > 0?}
    B2 -->|否| B2N([仅打日志])
    B2 -->|是| B3[SubmitAsync JSON candidates]
    B3 --> PP
  end

  subgraph DOM2[DOM]
    D0[脚本: identity/caption/media/candidates<br/>+ ADDRESS_DISCOVERY] --> D1[SubmitAsync / ProbePage JSON]
    D1 --> PP[ProbePageCoreAsync]
  end

  PC --> C1{会话 TryEnter 成功?}
  C1 -->|否| CX([丢弃])
  C1 -->|是| C2{SessionId 空或匹配当前?}
  C2 -->|否| CX
  C2 -->|是| C3{StatusCode 为 200/206/null?}
  C3 -->|否| CX
  C3 -->|是| C4{PageUrl 或 _page 存在?}
  C4 -->|否| CX
  C4 -->|是| C5{_page 已绑定且<br/>e.PageUrl 不同页?}
  C5 -->|是 跨页迟到| CX
  C5 -->|否| C6{URL 含 sabr=1 且<br/>无 mime=video/audio?}
  C6 -->|是| CX
  C6 -->|否| C6b{IsBrowserPlayEvidence?<br/>200/206 + Media 或强 MIME}
  C6b -->|是| C6c[登记 _browserObserved<br/>BrowserObserved=true]
  C6b -->|否| C7
  C6c --> C7{IsLikelySegment?<br/>且非 browserPlay}
  C7 -->|是| CX
  C7 -->|否| C8{IsCandidate?<br/>或 browserPlay}
  C8 -->|否| CX
  C8 -->|是| Q0[Queue]
  note1[ResourceType=Media 一律 IsCandidate=true<br/>忽略 Range 小 Content-Length]

  PP --> K1{TryEnter 成功?}
  K1 -->|否| KX([返回])
  K1 -->|是| K2{IsDirectMediaPage?}
  K2 -->|是| Q0
  K2 -->|否| K3{有 pageScriptJson?}
  K3 -->|否| K9
  K3 -->|是| K4{JSON 可解析?}
  K4 -->|否| K9
  K4 -->|是| K5[写 identity / caption / author]
  K5 --> K6{media[] 项}
  K6 -->|非 URI / 分片 / JunkPath| K6N([跳过项])
  K6 -->|通过| Q0
  K5 --> K7{candidates[] 项}
  K7 -->|无绝对 url| K7N([跳过])
  K7 -->|contentIdentity 与当前归属冲突| K7R([跳过：归属不匹配])
  K7 -->|通过| Q0
  K6N --> K9
  K7N --> K9
  K7R --> K9
  K9[Publish 非强制] --> K10{runExternal?}
  K10 -->|否| KX
  K10 -->|是| EXT_GATE{见第 3 节}

  Q0 --> Q1{scheme http/https?}
  Q1 -->|否| QX([丢弃])
  Q1 -->|是| Q2{非强 MIME 且过小<br/>且非 m3u8/mpd<br/>且非 range/videoplayback?}
  Q2 -->|是| QX
  Q2 -->|否| Q3{continuation lease?}
  Q3 -->|否| QX
  Q3 -->|是| Q4{去重键已存在?<br/>primary: 或 network: + Normalize}
  Q4 -->|是| QX
  Q4 -->|否| Q5[占 ffprobe 槽 / 90s budget]
  Q5 --> Q6{BrowserObserved?}
  Q6 -->|是| Q6A[FromBrowserObservation<br/>跳过 ffprobe/再 GET]
  Q6 -->|否| INS[InspectAsync]
  Q6A --> PUB[Publish]
  INS --> PUB
```
### `IsCandidate` 子分支

```mermaid
flowchart TD
  A[IsCandidate] --> B{http/https?}
  B -->|否| NO([false])
  B -->|是| C{m3u8/mpd 或 mpegurl/dash+xml?}
  C -->|是| YES([true])
  C -->|否| D{强 MIME video/audio?}
  D -->|是| YES
  D -->|否| E{LooksLikeJunkPath?}
  E -->|是| NO
  E -->|否| F{resourceType == Media?}
  F -->|是| YES
  F -->|否| G{有媒体扩展名?}
  G -->|是| G2{已知长度过小?}
  G2 -->|是| NO
  G2 -->|否| YES
  G -->|否| H{query/path 媒体启发式?<br/>mime=video、playurl、playAddr…}
  H -->|是| H2{有足够 ContentLength<br/>或 Media 类型?}
  H2 -->|是| YES
  H2 -->|否| NO
  H -->|否| NO
```

> **TikTok 优先策略（2026-09-08）：** CDP 实播（`BrowserObserved`）> DOM/playAddr > yt-dlp > 独立 `MediaAvailabilityValidator`。浏览器已成功 200/206 的 Media 不再被二次抽样否决。
### `InspectAsync` 子分支

```mermaid
flowchart TD
  INS[InspectAsync] --> I1{允许 resolveManifest<br/>且为 HLS/DASH?}
  I1 -->|是| I2[ResolveHls / ResolveDash]
  I2 --> I3{清单类型?}
  I3 -->|Master + codecs| I3M[按 codecs → Video/Audio/Combined/Unknown]
  I3 -->|Media + clear-key AES<br/>且非 DRM| I3A[HlsMedia 挂载<br/>Kind=Combined IsValidated]
  I3 -->|Media 明文| I3P[Kind=Unknown]
  I3 -->|Widevine 等 DRM| I3D[IsDrmProtected<br/>不挂 clear-key Hls]
  I3M --> I4
  I3A --> I4
  I3P --> I4
  I3D --> I4
  I4{逐 track: Kind != Unknown?}
  I4 -->|已是| I5[保留 track]
  I4 -->|Unknown| I6[ffprobe resolveManifest=false]
  I6 --> I6a{成功?}
  I6a -->|否| I6N([该 track 失败])
  I6a -->|是| I5
  I5 --> I7{variant 全部 track 成功?}
  I7 -->|否| NULL([Probed=null])
  I7 -->|是| OK[写入 _media → Publish]
  I6N --> I7

  I1 -->|否| F1[ffprobe 直链]
  F1 --> F2{exit=0 且有音/视频流<br/>且非 image/_pipe?}
  F2 -->|否| NULL
  F2 -->|是| F3{结果仍像清单且可再解析?}
  F3 -->|是且 resolveManifest| I2
  F3 -->|否| OK
```

| 判断 | 代码位置 |
|------|----------|
| ProcessCore 过滤链 | `UnifiedMediaPipeline.ProcessCoreAsync` |
| IsCandidate | `UnifiedMediaPipeline.IsCandidate` |
| Queue 去重/过小 | `UnifiedMediaPipeline.Queue` |
| HLS AES | `HlsManifestParser.TryParseClearKeyMedia` |
| Inspect 清单 | `InspectManifestAsync` |

---

## 3. 外置解析拆解（yt-dlp → ProbeSampleGate）

```mermaid
flowchart TD
  K10[ProbePage runExternal] --> Y0{ShouldRunExternalResolve?<br/>稳定内容地址 或<br/>identity 匹配 content:数字/BV}
  Y0 -->|否 Y3| Y0S([跳过：home_or_feed_without_concrete_video])
  Y0 -->|是| Y1[ResolveExternalPageUrl<br/>信息流根路径可改写为 /video/id]
  Y1 --> EXT[TryExternalResolveAsync]

  EXT --> E0{ExternalResolvers.Enabled?}
  E0 -->|否| E0N([external_disabled])
  E0 -->|是| E1{存在 IsAvailable 的 resolver?}
  E1 -->|否| E1N([未找到可用 yt-dlp])
  E1 -->|是| E2[foreach resolver]

  E2 --> E3{generation/Session 仍当前?}
  E3 -->|否 C1| E3N([丢弃写回])
  E3 -->|是| E4[ResolveAsync]
  E4 --> E5{LastFailureIsHumanVerification? Y1}
  E5 -->|是| E5N([停止后续客户端轮询<br/>记 HUMAN_VERIFICATION])
  E5 -->|否| E6{返回 videos 非空?}
  E6 -->|否| E6N[记 LastError / 试下一个 resolver]
  E6 -->|是| E7[逐条视频]

  E7 --> E8{页归属 id:X 且<br/>SiteContentId 存在且 ≠ X?}
  E8 -->|是| E8N([ownership_mismatch])
  E8 -->|否| E8b[StampBrowserObserved<br/>URL/会话匹配则标 BrowserObserved]
  E8b --> G[ProbeSampleGate.ValidateAndRecoverAsync]
  E8N --> E7

  G --> G0{isCurrentSession?}
  G0 -->|否| G0N([Cancelled])
  G0 -->|是| G1[拆分 videoCandidates / audioCandidates]

  G1 --> G2{还有未接受的视频变体?}
  G2 -->|否| G10
  G2 -->|是| G2b{变体含 BrowserObserved?}
  G2b -->|是| G3A[样本直接 OK<br/>不二次 GET]
  G2b -->|否| G3{变体含 hls/dash?}
  G3 -->|是| G3A
  G3 -->|否| G4[MediaAvailabilityValidator 抽样]
  G4 --> G5{抽样成功?}
  G5 -->|是| G3A
  G5 -->|否| G6[记 videoFailure]
  G6 --> G7{错误码 == HTTP_403?}
  G7 -->|否| G2
  G7 -->|是 T1| G8{同批 Compatible 备用<br/>≤ MaxAlternateTries=4?}
  G8 -->|有可用| G3A
  G8 -->|全失败| G9{MaxRecoveryResolves 未用完?}
  G9 -->|是| G9R[Renew：再 Resolve 固定页<br/>+ 再抽样]
  G9R --> G9a{isCurrentSession?}
  G9a -->|否| G0N
  G9a -->|是| G9b{恢复轨成功?}
  G9b -->|是| G3A
  G9b -->|否| G2
  G9 -->|否| G2

  G3A --> G2
  G10{usable 含视频?}
  G10 -->|是| G11[可附带音频轨]
  G10 -->|否| G12{有可用音频? T2}
  G12 -->|是| G12A[AudioOnly 保留<br/>保留 VideoDenied/失败原因]
  G12 -->|否| G12N([无可用变体])

  G11 --> E9
  G12A --> E9
  G12N --> E6N

  E9{Session/SamePage 仍匹配? C1}
  E9 -->|否| E3N
  E9 -->|是| E10{videos 仍空?}
  E10 -->|是| E6N
  E10 -->|否| E11[写入 _externalVideos]
  E11 --> PUB[Publish]
```

### 外置相关常量与语义

| 项 | 值 / 含义 |
|----|-----------|
| `MaxAlternateTries` | 4（同批备用） |
| `MaxRecoveryResolves` | 1（重新解析次数） |
| Y1 | 人机验证：停客户端轮询，明确错误码 |
| Y3 | 首页/信息流无具体身份：不调 yt-dlp |
| T1 | 视频 403：备用 → renew |
| T2 | 仅音频：部分成功，不清视频失败原因 |
| C1 | 恢复/校验结果不得写回已切换的新页 |

---

## 4. Publish / 列表展示拆解

```mermaid
flowchart TD
  SRC1[_media 探测成功] --> PUB
  SRC2[_externalVideos] --> PUB
  SRC3[CompleteDiscovery<br/>会话 Phase=Completed] --> PUBF[PublishCore forceEmit=true]
  PUB[Publish / PublishCore] --> U0
  PUBF --> U0

  U0{generation 已取消?}
  U0 -->|是| UX([不发 UI])
  U0 -->|否| U1[按页过滤 _media]
  U1 --> U2{当前页有 owner 归属?}
  U2 -->|是| U3[丢弃 ContentIdentity 冲突的本地轨]
  U2 -->|否| U4
  U3 --> U4[取同页 external DetectedVideo]
  U4 --> U5{external 存在且有 owner?}
  U5 -->|是| U6[互补轨拼入 probed<br/>单轨或非 hls/dash 多轨]
  U5 -->|否| U7
  U6 --> U7{probed 非空?}
  U7 -->|是| U8[BuildAggregatedVideo → local]
  U7 -->|否| U9[local=null]
  U8 --> U10[MergeVideos local+external]
  U9 --> U10
  U10 --> U11{merged 非空且 Variants>0?}
  U11 -->|否| UX
  U11 -->|是| U12[RemoveSupersededVideoOnly<br/>有非 FLV 视频时降权/剔除 FLV]
  U12 --> U13{generation 仍有效?}
  U13 -->|否| UX
  U13 -->|是| U14[绑定 VideoId/SessionId<br/>RecoveryPageUrl / Alternatives]
  U14 --> U15{有 _title?}
  U15 -->|是| U16[覆盖 DisplayTitle]
  U15 -->|否| U17
  U16 --> U17[缓存 _lastBuilt]
  U17 --> U18{forceEmit 或<br/>Phase == Completed?}
  U18 -->|否| U18D([延迟展示：探测中不刷列表])
  U18 -->|是| U19[EmitBuilt]
  U19 --> U20[VideoDetected / VideoUpdated / PageProbed]
  U20 --> U21([UI DetectedVideos<br/>视频/音轨模式 + 清晰度变体])
```

### UI 侧消费

| 事件 | `MainViewModel` 行为 |
|------|----------------------|
| `VideoDetected` | 插入/替换卡片；`_forceReplaceResults` 时清旧 |
| 手动探测结束且 Count=0 | 展示 `LastValidationError` 或 `LastExternalError` |
| 自动轮结束且 Count=0 | `status.probeEmpty` / 带外置提示 |

---

## 5. 端到端时序（简）

```mermaid
sequenceDiagram
  participant UI as MainViewModel
  participant WV as WebView2Host
  participant P as UnifiedMediaPipeline
  participant Y as YtDlpResolver
  participant G as ProbeSampleGate

  UI->>UI: StartPageDetectionSession
  UI->>WV: RefreshContext + ProbeCurrentPage
  WV->>P: SubmitAsync DOM JSON
  WV-->>P: ProcessAsync 网络事件（并行）
  P->>P: Queue → Inspect → _media
  UI->>P: ProbePageAsync runExternal
  alt ShouldRunExternalResolve
    P->>Y: ResolveAsync
    Y-->>P: DetectedVideo[]
    P->>G: ValidateAndRecover
    G-->>P: 可用 / AudioOnly / null
    P->>P: _externalVideos + Publish 缓存
  else 首页/无身份
    P-->>P: skip external
  end
  UI->>WV: ProbeCurrentPage 再次
  UI->>P: CompleteDiscoveryAsync
  P->>UI: EmitBuilt → 可下载列表
```

---

## 6. 相关源码索引

| 符号 | 路径 |
|------|------|
| `StartPageDetectionSession` / `RunPagePassAsync` | `src/VideoDownloader.UI/ViewModels/MainViewModel.cs` |
| `ProbeCurrentPageAsync` / CDP | `src/VideoDownloader.Infrastructure/Browser/WebView2Host.cs` |
| `ProcessCoreAsync` / `Queue` / `Inspect*` / `PublishCore` | `src/VideoDownloader.Infrastructure/Detection/UnifiedMediaPipeline.cs` |
| `ShouldRunExternalResolve` | 同上 |
| `ProbeSampleGate` | `src/VideoDownloader.Infrastructure/Detection/ProbeSampleGate.cs` |
| `YtDlpResolver` | `src/VideoDownloader.Infrastructure/Sites/ExternalResolvers/YtDlpResolver.cs` |
| `HlsManifestParser` / clear-key | `src/VideoDownloader.Core/Manifests/HlsManifestParser.cs` |
| `DetectionSession` 相位 | `src/VideoDownloader.Core/Detection/DetectionSession.cs` |

---

## 7. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-09-08 | 初版：总览 + 网络入队 / 外置 / Publish 三拆解，对齐 AES clear-key、Y1–C1、延迟 Emit |
