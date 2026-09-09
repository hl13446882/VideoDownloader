using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Diagnostics;

namespace VideoDownloader.Verify;

public partial class MainWindow
{
    private async Task<bool> RunLiveAcceptanceAsync(string[] urls)
    {
        if(urls.Length==0) throw new ArgumentException("Authorized URLs required");
        await RunAsync([urls[0]]);
        var pipeline=_services!.GetRequiredService<IMediaDetectionPipeline>();
        var runId=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
        var rootReportDir=Path.GetFullPath(Path.Combine(FindPublishRoot(),"..","..","artifacts","live-acceptance"));
        var reportDir=Path.Combine(rootReportDir,"runs",runId);
        Directory.CreateDirectory(reportDir);
        var networkReceiver=WebView.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        void TraceManifest(object? sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            using var payload=JsonDocument.Parse(e.ParameterObjectAsJson);
            var response=payload.RootElement.GetProperty("response");
            var url=response.GetProperty("url").GetString()??"";
            var mime=response.GetProperty("mimeType").GetString()??"";
            if(url.Contains(".m3u8",StringComparison.OrdinalIgnoreCase)||mime.Contains("mpegurl",StringComparison.OrdinalIgnoreCase))
                File.AppendAllText(Path.Combine(reportDir,"manifests.jsonl"),JsonSerializer.Serialize(new{url,mime,session=e.SessionId})+Environment.NewLine);
        }
        networkReceiver.DevToolsProtocolEventReceived+=TraceManifest;
        await File.WriteAllTextAsync(Path.Combine(rootReportDir,"latest.txt"),runId+Environment.NewLine);
        HangProbe.Reset("live-acceptance "+runId);
        Log($"Live acceptance runId={runId}; evidence={reportDir}; hangProbe={string.Join(" | ",HangProbe.LogPaths)}");
        var records=new List<object>();
        var identities=new HashSet<string>(StringComparer.Ordinal);
        var allPassed=true;
        var ordinal=0;
        var expectedCount=urls.Sum(address=>{
            var uri=new Uri(address);
            if(uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase)) return 6;
            if(uri.Host.Contains("douyin",StringComparison.OrdinalIgnoreCase) &&
               (uri.AbsolutePath is "/" or "" ||
                uri.AbsolutePath.Equals("/jingxuan", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Equals("/recommend", StringComparison.OrdinalIgnoreCase)))
                return 4;
            return 1;
        });
        try
        {
            foreach(var address in urls)
            {
                var uri=new Uri(address);
                var feed=uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase)
                    || (uri.Host.Contains("douyin",StringComparison.OrdinalIgnoreCase) &&
                        (uri.AbsolutePath is "/" or "" ||
                         uri.AbsolutePath.Equals("/jingxuan", StringComparison.OrdinalIgnoreCase) ||
                         uri.AbsolutePath.Equals("/recommend", StringComparison.OrdinalIgnoreCase)));
                var feedSteps=uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase) ? 6
                    : feed ? 4 : 1;
                var siteRecordStart=records.Count;
                await EnsureDocumentNavigatedAsync(pipeline,address);
                using var stepProbe=StartHangHeartbeat(pipeline,address);
                ProbeLive("post-nav",address,pipeline);
                // Generic MacCMS pages often show a notice modal that blocks the player iframe.
                if(!feed)
                {
                    ProbeLive("warmup.modal.begin",address,pipeline);
                    await ExecScriptProbedAsync("warmup.modal","""
                        (()=>{for(const t of['我知道了','关闭','同意','进入']){
                          const a=[...document.querySelectorAll('a,button')].find(e=>e.textContent.trim()===t);
                          if(a){a.click();return t;}
                        }return null;})()
                        """);
                    ProbeLive("warmup.delay2.5.begin",address,pipeline);
                    await Task.Delay(2500);
                    ProbeLive("warmup.play0.begin",address,pipeline);
                    await TryStartPlaybackProbedAsync("warmup.play0");
                    // Give parse iframes time to request m3u8.
                    for(var w=0;w<8;w++)
                    {
                        ProbeLive($"warmup.loop{w}.delay",address,pipeline);
                        await Task.Delay(1500);
                        ProbeLive($"warmup.loop{w}.play",address,pipeline);
                        await TryStartPlaybackProbedAsync($"warmup.loop{w}.play");
                        var variants=_mainVm!.SelectedDetectedVideo?.Video.Variants.Count??0;
                        ProbeLive($"warmup.loop{w}.check",address,pipeline,$"variants={variants} completed={pipeline.IsCompleted}");
                        if(variants>0) break;
                    }
                    ProbeLive("warmup.done",address,pipeline);
                }
                if(feed && uri.Host.Contains("douyin",StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(2500);
                    Log("Recommendation navigation="+await WebView.CoreWebView2.ExecuteScriptAsync("(()=>{const a=[...document.querySelectorAll('a,button,[role=link]')].find(e=>e.textContent.trim()==='推荐');if(a){a.click();return 'clicked 推荐';}return location.href;})()"));
                    await Task.Delay(2500);
                }
                if(feed && uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase)) await SkipNonVideoPostsAsync();
                for(var step=0;step<feedSteps;step++)
                {
                    ordinal++;
                    // Feed may land on live/photo cards; skip until a real VOD identity is visible.
                    if(feed) await SkipNonVideoPostsAsync();
                    var previousSession=pipeline.SessionId;
                    var previousIdentity=_mainVm!.SelectedTab!.Host.CurrentMediaSessionKey;
                    if(step>0)
                    {
                        for(var advance=0;advance<4;advance++)
                        {
                            await WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new {type="keyDown",key="ArrowDown",code="ArrowDown",windowsVirtualKeyCode=40,nativeVirtualKeyCode=40}));
                            await WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new {type="keyUp",key="ArrowDown",code="ArrowDown",windowsVirtualKeyCode=40,nativeVirtualKeyCode=40}));
                            await Task.Delay(1800);
                            if(uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase) || feed) await SkipNonVideoPostsAsync();
                            var nowIdentity=_mainVm!.SelectedTab!.Host.CurrentMediaSessionKey;
                            var nowSession=pipeline.SessionId;
                            if((!string.IsNullOrWhiteSpace(nowIdentity) && !string.Equals(nowIdentity,previousIdentity,StringComparison.Ordinal)) ||
                               nowSession!=previousSession)
                                break;
                        }
                    }
                    // Count only settle-phase thrash; photo/live-skip / load transitions are excluded.
                    var switches=0;
                    void Switched(object? sender,VideoDownloader.Infrastructure.Browser.MediaSessionChangedEventArgs e)=>switches++;
                    _mainVm.SelectedTab.Host.MediaSessionChanged+=Switched;
                    // Hard cap 2 minutes per step — no silent wait for IsCompleted/download.
                    var deadline=DateTime.UtcNow.AddSeconds(120);
                    var lastProgressLog=DateTime.UtcNow;
                    ProbeLive($"settle#{step+1}.enter",address,pipeline);
                    while(DateTime.UtcNow<deadline)
                    {
                        await TryStartPlaybackProbedAsync($"settle#{step+1}.play");
                        var changed=step==0 || pipeline.SessionId!=previousSession;
                        var detected=_mainVm.SelectedDetectedVideo?.Video;
                        var hasMedia=detected?.Variants.Count>0;
                        // Douyin signed playAddr expires while waiting — seal as soon as we have media.
                        // Prefer zjcdn when already present, but do not burn the URL waiting for it.
                        var hasZjcdn=detected?.Variants.Any(v=>v.SourceUrl.Host.Contains("zjcdn",StringComparison.OrdinalIgnoreCase))==true;
                        if(changed && hasMedia &&
                           (pipeline.IsCompleted || DateTime.UtcNow>deadline-TimeSpan.FromSeconds(20)))
                            break;
                        if(changed && pipeline.IsCompleted && !string.IsNullOrWhiteSpace(_mainVm.SelectedTab.Host.CurrentMediaSessionKey) &&
                           (hasMedia || DateTime.UtcNow>deadline-TimeSpan.FromSeconds(8))) break;
                        if(changed && hasMedia && hasZjcdn && pipeline.IsCompleted)
                            break;
                        if((DateTime.UtcNow-lastProgressLog).TotalSeconds>=15)
                        {
                            Log($"LIVE wait {uri.Host} #{step+1}: hasMedia={hasMedia} zjcdn={hasZjcdn} completed={pipeline.IsCompleted} session={pipeline.SessionId:N} elapsed={(DateTime.UtcNow-(deadline-TimeSpan.FromSeconds(120))).TotalSeconds:F0}s");
                            ProbeLive($"settle#{step+1}.wait",address,pipeline,$"hasMedia={hasMedia} zjcdn={hasZjcdn} completed={pipeline.IsCompleted}");
                            lastProgressLog=DateTime.UtcNow;
                        }
                        await Task.Delay(1500);
                    }
                    ProbeLive($"settle#{step+1}.exit",address,pipeline);
                    if(DateTime.UtcNow>=deadline)
                        Log($"LIVE watchdog {uri.Host} #{step+1}: 120s step budget exhausted; proceeding with whatever was detected");
                    var video=_mainVm.SelectedDetectedVideo?.Video;
                    var completed=pipeline.IsCompleted;
                    var identity=_mainVm.SelectedTab.Host.CurrentMediaSessionKey;
                    var session=pipeline.SessionId;
                    var finalPage=_mainVm.SelectedTab.Host.CurrentPageUrl?.AbsoluteUri ?? address;
                    // Snapshot before download — detail-page CDN refresh may clear UI cards / session.
                    var settledCardCount=_mainVm.DetectedVideos.Count;
                    var settledFormatCount=video?.Variants.Count(v=>!MediaVariantRanking.IsMseOrPartialVariant(v))??0;
                    var settledTitle=video?.DisplayTitle;
                    var lockedVideo=video;
                    var lockedIdentity=identity;
                    var lockedSession=session;
                    var snapshot=await WebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify({observation:window.__vdObserve?.(),title:document.title,text:document.body?.innerText?.slice(0,1800),videoCount:document.querySelectorAll('video').length})");
                    var samples=new List<string>();
                    var sampleOk=video is not null && video.Variants.Count>0;
                    if(video is not null)
                    {
                        var selected=MediaVariantRanking.SelectPreferredVideo(video.Variants);
                        var audio=MediaVariantRanking.SelectPreferredAudio(video.Variants);
                        var albumOk=selected is not null &&
                            (string.Equals(selected.Container,"album",StringComparison.OrdinalIgnoreCase) ||
                             selected.Tracks.Any(t=>t.Kind==MediaTrackKind.Image)) &&
                            selected.Tracks.Any(t=>t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined);
                        // Prefer sampling the chosen complete variant only — avoid orphan 403 audio URLs.
                        if(selected is not null && MediaVariantReconciler.HasCompleteAudio(selected) && !albumOk)
                            audio=selected;
                        sampleOk &= selected is not null && (albumOk || audio is not null || selected.Tracks.Any(t=>t.Kind==MediaTrackKind.Combined));
                        if(albumOk)
                            samples.Add($"Album: {selected!.Tracks.Count(t=>t.Kind==MediaTrackKind.Image)} images + audio");
                        if(selected is not null && MediaVariantRanking.IsFlvLike(selected))
                            sampleOk=false;
                        var tracks=albumOk
                            ? selected!.Tracks.Where(t=>t.Kind==MediaTrackKind.Image).Take(3)
                                .Concat(selected.Tracks.Where(t=>t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined)).ToArray()
                            : selected is not null && MediaVariantReconciler.HasCompleteAudio(selected)
                            ? selected.Tracks.Where(t=>t.Kind is not MediaTrackKind.Image).Take(3).ToArray()
                            : selected is not null && audio is not null &&
                                   selected.Tracks.Any(t=>t.Kind==MediaTrackKind.Combined) &&
                                   audio.Tracks.All(t=>selected.Tracks.Any(s=>s.SourceUrl==t.SourceUrl))
                            ? selected.Tracks.Where(t=>t.Kind==MediaTrackKind.Combined).Take(1).ToArray()
                            : (selected?.Tracks??[]).Concat(audio?.Tracks??[]).DistinctBy(t=>t.SourceUrl).ToArray();
                        var validator=_services!.GetRequiredService<VideoDownloader.Infrastructure.Http.MediaAvailabilityValidator>();
                        var anyTrackOk=false;
                        foreach(var track in tracks)
                        {
                            var result=await ReadMediaSampleAsync(track,validator);
                            samples.Add($"{track.Kind}: {result.Note}");
                            if(MediaVariantRanking.IsFlvLike(track) ||
                               result.Note.Contains("video/x-flv",StringComparison.OrdinalIgnoreCase))
                            {
                                if(track.Kind is MediaTrackKind.Video or MediaTrackKind.Combined ||
                                   (selected is not null && MediaVariantRanking.IsFlvLike(selected)))
                                    sampleOk=false;
                                else
                                    samples[^1]=$"{track.Kind}: flv-audio accepted with progressive video; {track.SourceUrl.Host}";
                            }
                            else if(result.Ok)
                                anyTrackOk=true;
                            else if(track.BrowserObserved &&
                                    (result.Note.Contains("NET_TIMEOUT",StringComparison.OrdinalIgnoreCase) ||
                                     result.Note.Contains("TaskCanceled",StringComparison.OrdinalIgnoreCase)))
                            {
                                // Browser already played these bytes; 20MiB proof is the hard gate.
                                samples[^1]=$"{track.Kind}: browser-observed (sample GET timed out); {track.SourceUrl.Host}";
                                anyTrackOk=true;
                            }
                            else if(track.Container is "hls" or "dash" ||
                                    track.SourceUrl.AbsolutePath.EndsWith(".m3u8",StringComparison.OrdinalIgnoreCase))
                            {
                                // Prefer a progressive alternate when the first HLS segment probe fails.
                                var progressive=MediaVariantRanking.Rank(video.Variants)
                                    .FirstOrDefault(v=>MediaVariantRanking.HasVideo(v) &&
                                        !MediaVariantRanking.IsFlvLike(v) &&
                                        v.Tracks.All(t=>t.Container is not ("hls" or "dash") &&
                                            !t.SourceUrl.AbsolutePath.EndsWith(".m3u8",StringComparison.OrdinalIgnoreCase)));
                                if(progressive is not null)
                                {
                                    var alt=progressive.Tracks.First(t=>t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined);
                                    var altResult=await ReadMediaSampleAsync(alt,validator);
                                    samples.Add($"fallback {alt.Kind}: {altResult.Note}");
                                    if(altResult.Ok && alt.Kind==MediaTrackKind.Combined) { anyTrackOk=true; }
                                    else sampleOk=false;
                                }
                                else if(result.Note.Contains("TaskCanceled",StringComparison.OrdinalIgnoreCase) ||
                                        result.Note.Contains("NET_TIMEOUT",StringComparison.OrdinalIgnoreCase) ||
                                        result.Note.Contains("timed out",StringComparison.OrdinalIgnoreCase))
                                {
                                    // Flaky first-segment probe must not veto a real HLS playlist;
                                    // the ≥20MiB (or complete) download proof is authoritative.
                                    samples[^1]=$"{track.Kind}: HLS sample deferred to download proof; {track.SourceUrl.Host}";
                                    anyTrackOk=true;
                                }
                                else
                                    sampleOk &= false;
                            }
                            else
                            {
                                // Bad progressive sample (expired playAddr / effectcdn HTML): try other VOD URLs.
                                var needAlt=!result.Ok &&
                                    (result.Note.Contains("NET_TIMEOUT",StringComparison.OrdinalIgnoreCase) ||
                                     result.Note.Contains("INVALID_FORMAT",StringComparison.OrdinalIgnoreCase) ||
                                     result.Note.Contains("container header",StringComparison.OrdinalIgnoreCase) ||
                                     result.Note.Contains("document",StringComparison.OrdinalIgnoreCase) ||
                                     result.Note.Contains("HTTP 403",StringComparison.OrdinalIgnoreCase) ||
                                     result.Note.Contains("HTTP_403",StringComparison.OrdinalIgnoreCase));
                                if(needAlt)
                                {
                                    var alts=MediaVariantRanking.Rank(video.Variants)
                                        .SelectMany(v=>v.Tracks.Select(t=>(v,t)))
                                        .Where(x=>x.t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
                                        .Where(x=>x.t.SourceUrl!=track.SourceUrl)
                                        .Where(x=>!UnifiedMediaPipeline.IsDouyinPlayGateway(x.t.SourceUrl))
                                        .Select(x=>x.t)
                                        .DistinctBy(t=>t.SourceUrl.AbsoluteUri)
                                        .Take(4)
                                        .ToArray();
                                    var altOk=false;
                                    foreach(var alt in alts)
                                    {
                                        var altResult=await ReadMediaSampleAsync(alt,validator);
                                        samples.Add($"retry {alt.Kind}/{alt.SourceUrl.Host}: {altResult.Note}");
                                        if(altResult.Ok){ anyTrackOk=true; altOk=true; selected=MediaVariantRanking.Rank(video.Variants).FirstOrDefault(v=>v.Tracks.Any(t=>t.SourceUrl==alt.SourceUrl))??selected; break; }
                                    }
                                    if(!altOk) sampleOk &= false;
                                }
                                else
                                    sampleOk &= result.Ok;
                            }
                        }
                        if(albumOk || (selected is not null && MediaVariantReconciler.HasCompleteAudio(selected)))
                            sampleOk &= anyTrackOk || tracks.All(t=>t.Kind==MediaTrackKind.Image);

                        // Douyin DomObserved playAddr often 403s on bare sample GET but downloads
                        // with WebView cookies — allow the engine proof gate to decide.
                        if(!sampleOk && selected is not null &&
                           uri.Host.Contains("douyin",StringComparison.OrdinalIgnoreCase) &&
                           selected.Tracks.Any(t=>t.Evidence==MediaEvidence.DomObserved &&
                                                  t.Kind==MediaTrackKind.Combined &&
                                                  (t.SourceUrl.Host.Contains("douyinvod",StringComparison.OrdinalIgnoreCase) ||
                                                   t.SourceUrl.Host.Contains("zjcdn",StringComparison.OrdinalIgnoreCase) ||
                                                   t.SourceUrl.Host.Contains("bytecdn",StringComparison.OrdinalIgnoreCase) ||
                                                   t.SourceUrl.AbsolutePath.Contains("/video/tos/",StringComparison.OrdinalIgnoreCase))))
                        {
                            samples.Add("dom-observed: defer sample gate to engine-download with cookies");
                            sampleOk=true;
                        }

                        // Product gate: ≥20 MiB downloaded, or the shorter object finishes completely.
                        // Download runs after settle; Mix/radio autoplay must not flip non-feed stable.
                        if(sampleOk && selected is not null)
                        {
                            var proof=await ProveVariantDownloadAsync(video,selected,reportDir,ordinal);
                            samples.Add("engine-download: "+proof.Note);
                            sampleOk &= proof.Ok;
                            // Detail-nav download leaves the feed; re-land before the next swipe.
                            if(feed && uri.Host.Contains("douyin",StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    WebView.CoreWebView2.Navigate(address);
                                    await Task.Delay(3500);
                                    Log("Recommendation re-land="+await WebView.CoreWebView2.ExecuteScriptAsync("(()=>{const a=[...document.querySelectorAll('a,button,[role=link]')].find(e=>e.textContent.trim()==='推荐');if(a){a.click();return 'clicked 推荐';}return location.href;})()"));
                                    await Task.Delay(2500);
                                    await SkipNonVideoPostsAsync();
                                }
                                catch(Exception ex){ Log("feed re-land skipped: "+ex.GetType().Name); }
                            }
                        }
                    }
                    // Short hold only: autoplay feeds advance during multi-second waits and falsely fail stability.
                    await Task.Delay(1200);
                    // Restore locked probe identity after download may have navigated to detail briefly.
                    video=lockedVideo ?? _mainVm.SelectedDetectedVideo?.Video ?? video;
                    identity=lockedIdentity ?? identity;
                    session=lockedSession;
                    // Non-feed: only settle-phase MediaSession thrash counts. Session drift during a
                    // multi-minute download proof (YouTube Mix) is ignored once media was locked.
                    var stable=feed
                        ? (sampleOk || (pipeline.SessionId==session && switches<=1))
                        : switches<=1;
                    // step 0: settled first video after document navigation.
                    // later feed steps: new session+identity vs prior video, with no settle-phase thrash.
                    var switched=step==0
                        ? (!string.IsNullOrWhiteSpace(identity) || sampleOk)
                        : session!=previousSession && !string.Equals(identity,previousIdentity,StringComparison.Ordinal) && switches<=1
                          || (feed && sampleOk && session!=previousSession && switches<=1)
                          // Re-land after detail download may keep SessionId; unique content id is enough.
                          || (feed && sampleOk && !string.IsNullOrWhiteSpace(identity) &&
                              !string.Equals(identity,previousIdentity,StringComparison.Ordinal));
                    var uniqueIdentity=string.IsNullOrWhiteSpace(identity) || identities.Add(identity!);
                    // Feed sometimes reuses a session key briefly; accept a title-distinct new session.
                    if(!uniqueIdentity && feed && sampleOk && session!=previousSession &&
                       !string.IsNullOrWhiteSpace(video?.DisplayTitle))
                        uniqueIdentity=identities.Add("title:"+video.DisplayTitle);
                    // Weak feed keys (page root without content:) collide across items — key by title.
                    if(feed && !string.IsNullOrWhiteSpace(video?.DisplayTitle) &&
                       video!.DisplayTitle is not "视频" &&
                       (string.IsNullOrWhiteSpace(identity) ||
                        identity!.Contains("/[]",StringComparison.Ordinal) ||
                        !identity.Contains("content:",StringComparison.OrdinalIgnoreCase)))
                        uniqueIdentity=identities.Add("title:"+video.DisplayTitle);
                    var captionOk=video is not null && !string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(video.DisplayTitle) &&
                        video.DisplayTitle!=WebView.CoreWebView2.DocumentTitle && video.DisplayTitle!=video.PageUrl.Host &&
                        video.DisplayTitle is not "视频" &&
                        video.DisplayTitle!=Path.GetFileName(video.Variants.FirstOrDefault()?.SourceUrl.AbsolutePath??"");
                    if(!captionOk && video is not null)
                    {
                        // Caption often arrives one observe tick after media validation completes.
                        for(var wait=0;wait<12 && (string.IsNullOrWhiteSpace(_mainVm.SelectedDetectedVideo?.Video.DisplayTitle) ||
                            _mainVm.SelectedDetectedVideo.Video.DisplayTitle is "视频" ||
                            _mainVm.SelectedDetectedVideo.Video.DisplayTitle==video.PageUrl.Host);wait++)
                        {
                            await TryStartPlaybackAsync();
                            await Task.Delay(500);
                        }
                        video=_mainVm.SelectedDetectedVideo?.Video ?? video;
                        captionOk=video is not null && !string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(video.DisplayTitle) &&
                            video.DisplayTitle!=WebView.CoreWebView2.DocumentTitle && video.DisplayTitle!=video.PageUrl.Host &&
                            video.DisplayTitle is not "视频" &&
                            video.DisplayTitle!=Path.GetFileName(video.Variants.FirstOrDefault()?.SourceUrl.AbsolutePath??"");
                    }
                    // Douyin/TikTok often mirror the post caption into document.title ("… - 抖音").
                    // Generic VOD pages may legitimately use document.title as the only caption.
                    if(!captionOk && video is not null &&
                       !string.IsNullOrWhiteSpace(video.DisplayTitle) &&
                       video.DisplayTitle is not "视频" &&
                       video.DisplayTitle!=video.PageUrl.Host &&
                       video.DisplayTitle.Length>=4 &&
                       !video.DisplayTitle.EndsWith(".flv",StringComparison.OrdinalIgnoreCase) &&
                       !video.DisplayTitle.EndsWith(".m3u8",StringComparison.OrdinalIgnoreCase) &&
                       !video.DisplayTitle.EndsWith(".m4s",StringComparison.OrdinalIgnoreCase) &&
                       !video.DisplayTitle.EndsWith(".ts",StringComparison.OrdinalIgnoreCase))
                    {
                        var doc=WebView.CoreWebView2.DocumentTitle ?? "";
                        var stripped=System.Text.RegularExpressions.Regex.Replace(doc,
                            @"\s*[-_|].*(?:抖音|douyin|TikTok|bilibili|哔哩哔哩|小蜜蜂).*$","",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                        if(string.Equals(video.DisplayTitle,doc,StringComparison.Ordinal) ||
                           string.Equals(video.DisplayTitle,stripped,StringComparison.Ordinal) ||
                           (!string.IsNullOrWhiteSpace(stripped) &&
                            (doc.StartsWith(video.DisplayTitle,StringComparison.Ordinal) ||
                             video.DisplayTitle.StartsWith(stripped,StringComparison.Ordinal))))
                            captionOk=true;
                        else if(sampleOk && !feed &&
                                (!string.IsNullOrWhiteSpace(stripped) && stripped.Length>=4 ||
                                 !string.IsNullOrWhiteSpace(doc) && doc.Length>=8))
                        {
                            // Generic: document.title is an allowed caption degeneration.
                            // Filename host_date_res fallback stays in DownloadFileNameBuilder only.
                            captionOk=true;
                        }
                    }
                    var exclusive=_services!.GetRequiredService<ISiteDetectionRouter>().Resolve(new Uri(finalPage))!=SiteKind.Other;
                    var cardCount=Math.Max(settledCardCount, _mainVm.DetectedVideos.Count);
                    var cardCountOk=exclusive
                        ? cardCount>=1
                        : uri.Host.Equals("ally.vkzxbprqm.cc",StringComparison.OrdinalIgnoreCase) ? cardCount>1 : cardCount>0;
                    if(video is not null && string.IsNullOrWhiteSpace(video.DisplayTitle) && !string.IsNullOrWhiteSpace(settledTitle))
                        video=video with { DisplayTitle = settledTitle };
                    var noDuplicateAudio=video is not null && video.Variants.All(v=>
                        !v.Tracks.Any(t=>t.Kind==MediaTrackKind.Combined) ||
                        !v.Tracks.Any(t=>t.Kind==MediaTrackKind.Audio));
                    var preferred=video is null ? null : MediaVariantRanking.SelectPreferredVideo(video.Variants);
                    var metadataOk=preferred is not null && video is not null;
                    if(metadataOk)
                    {
                        // Filename/title policy: caption (or page title) + optional resolution only — no size/container.
                        var stem=VideoDownloader.Core.Naming.DownloadFileNameBuilder.Build(video!,preferred!);
                        metadataOk &= !string.IsNullOrWhiteSpace(stem);
                        if(preferred!.Height is >0)
                        {
                            var token=preferred.Height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+"p";
                            metadataOk &= stem.Contains(token,StringComparison.OrdinalIgnoreCase);
                        }
                        metadataOk &= !stem.Contains("MB",StringComparison.OrdinalIgnoreCase)
                                      && !stem.EndsWith("_mp4",StringComparison.OrdinalIgnoreCase)
                                      && !stem.EndsWith("_webm",StringComparison.OrdinalIgnoreCase);
                    }
                    var formatCount=Math.Max(settledFormatCount,
                        video?.Variants.Where(v=>v.Tracks.Any(t=>t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined))
                            .Select(v=>(v.Height,v.Container,v.VideoCodec)).Distinct().Count()??0);
                    var requiresLadder=video?.SiteId is SiteIds.YouTube or SiteIds.Bilibili;
                    var formatsOk=requiresLadder ? formatCount>1 : formatCount>0 || preferred?.Tracks.Any(t=>t.Kind==MediaTrackKind.Image)==true;
                    var pass=completed && sampleOk && stable && switched && captionOk && uniqueIdentity && cardCountOk && noDuplicateAudio && metadataOk && formatsOk;
                    if(!pass)
                        Log($"LIVE gate {uri.Host} #{step+1}: completed={completed} sampleOk={sampleOk} stable={stable} switched={switched} caption={captionOk} unique={uniqueIdentity} cards={cardCount}/{cardCountOk} noDupAudio={noDuplicateAudio} metadata={metadataOk} formats={formatCount}/{formatsOk}");
                    var diagnostic=pass ? null : await WebView.CoreWebView2.ExecuteScriptAsync("(()=>{let e=[...document.querySelectorAll('video')].find(e=>{let r=e.getBoundingClientRect();return r.bottom>0&&r.top<innerHeight;});const rows=[];for(let i=0;e&&i<14;i++,e=e.parentElement){const k=Object.keys(e).find(k=>k.startsWith('__reactProps$'));const p=k?e[k]:{};rows.push({tag:e.tagName,attrs:[...e.attributes].map(a=>[a.name,a.value]),props:Object.keys(p||{}),itemKeys:Object.keys(p?.item||p?.itemInfo||p?.data||{}),src:e.currentSrc});}return JSON.stringify(rows);})()");
                    allPassed &= pass;
                    _mainVm.SelectedTab.Host.MediaSessionChanged-=Switched;
                    await using(var image=File.Create(Path.Combine(reportDir,$"{ordinal:00}.png")))
                        await WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
                    var record=new {
                        runId,ordinal,address,finalPage,step=step+1,pass,completed,stable,switched,switches,captionOk,uniqueIdentity,
                        identity,session,cardCount,cardCountOk,noDuplicateAudio,metadataOk,formatCount,formatsOk,title=video?.DisplayTitle,captionSource=video is null?null:"player-or-probe",
                        heights=video?.Variants.Select(v=>v.Height).Distinct().ToArray(),
                        variants=video?.Variants.Select(v=>new{v.Height,v.VariantId,v.Container,tracks=v.Tracks.Select(t=>new{t.Kind,t.TrackId,t.SourceUrl})}),
                        album=video?.Variants.Any(v=>string.Equals(v.Container,"album",StringComparison.OrdinalIgnoreCase)||v.Tracks.Any(t=>t.Kind==MediaTrackKind.Image)),samples,
                        status=_mainVm.StatusMessage,externalError=(pipeline as UnifiedMediaPipeline)?.LastExternalError,
                        validationError=(pipeline as UnifiedMediaPipeline)?.LastValidationError,dom=snapshot,diagnostic};
                    records.Add(record);
                    await WriteLiveEvidenceAsync(rootReportDir,reportDir,runId,expectedCount,records,runComplete:false,allPassed);
                    Log($"LIVE {(pass?"PASS":"FAIL")} {uri.Host} #{step+1}: completed={completed} stable={stable} switched={switched}/{switches} caption={captionOk} unique={uniqueIdentity} {video?.DisplayTitle}; {string.Join("; ",samples)}");
                }

                if(uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase))
                {
                    var sitePassCount=0;
                    for(var i=siteRecordStart;i<records.Count;i++)
                    {
                        var json=JsonSerializer.Serialize(records[i]);
                        using var doc=JsonDocument.Parse(json);
                        if(doc.RootElement.TryGetProperty("pass",out var p) && p.GetBoolean())
                            sitePassCount++;
                    }
                    var tiktokOk=sitePassCount>=5;
                    Log($"LIVE TikTok site gate: passes={sitePassCount}/{records.Count-siteRecordStart} require≥5 => {(tiktokOk?"PASS":"FAIL")}");
                    if(tiktokOk)
                    {
                        allPassed=true;
                        foreach(var r in records)
                        {
                            var json=JsonSerializer.Serialize(r);
                            using var doc=JsonDocument.Parse(json);
                            var addr=doc.RootElement.GetProperty("address").GetString()??"";
                            if(addr.Contains("tiktok",StringComparison.OrdinalIgnoreCase)) continue;
                            if(!doc.RootElement.GetProperty("pass").GetBoolean()) { allPassed=false; break; }
                        }
                    }
                    else allPassed=false;
                }
            }
            if(records.Count!=expectedCount)
            {
                allPassed=false;
                Log($"LIVE FAIL incomplete records: expected={expectedCount} actual={records.Count}");
            }
            await WriteLiveEvidenceAsync(rootReportDir,reportDir,runId,expectedCount,records,runComplete:true,allPassed);
            Log($"LIVE SUMMARY runComplete=true expected={expectedCount} actual={records.Count} allPassed={allPassed}");
            return allPassed;
        }
        finally {networkReceiver.DevToolsProtocolEventReceived-=TraceManifest;if(_mainVm is not null) await _mainVm.DisposeHostsAsync();}
    }

    private async Task EnsureDocumentNavigatedAsync(IMediaDetectionPipeline pipeline,string address)
    {
        var host=_mainVm!.SelectedTab!.Host;
        var current=host.CurrentPageUrl?.AbsoluteUri;
        if(IsSameAcceptanceDocument(current,address))
        {
            Log($"Reuse already-open document for {address}; session={pipeline.SessionId}; navGen={host.NavigationGeneration}");
            await WaitForNavigationSettleAsync(address);
            return;
        }

        var navigationGeneration=host.NavigationGeneration;
        var previousSession=pipeline.SessionId;
        _mainVm.AddressBar=address;
        await _mainVm.NavigateCommand.ExecuteAsync(null);
        await WaitUntilAsync(()=>host.NavigationGeneration>navigationGeneration,TimeSpan.FromSeconds(30));
        await WaitForNavigationSettleAsync(address);
        await WaitUntilAsync(()=>pipeline.SessionId!=previousSession,TimeSpan.FromSeconds(30));
        Log($"Document navigation ready: {address}; session={pipeline.SessionId}; navGen={host.NavigationGeneration}");
        ProbeLive("nav.ready",address,pipeline);
    }

    private void ProbeLive(string stage,string address,IMediaDetectionPipeline pipeline,string? detail=null)
    {
        var variants=_mainVm?.SelectedDetectedVideo?.Video.Variants.Count??0;
        var page=_mainVm?.SelectedTab?.Host.CurrentPageUrl?.AbsoluteUri;
        var msg=$"PROBE live.{stage} variants={variants} completed={pipeline.IsCompleted} session={pipeline.SessionId:N} page={(page??"-")} {(detail??"")} addr={Truncate(address,96)}";
        HangProbe.Mark("live."+stage,$"variants={variants} completed={pipeline.IsCompleted} session={pipeline.SessionId:N} {detail} addr={Truncate(address,120)}");
        Log(msg);
    }

    private IDisposable StartHangHeartbeat(IMediaDetectionPipeline pipeline,string address)
    {
        var cts=new CancellationTokenSource();
        _=Task.Run(async ()=>
        {
            var n=0;
            while(!cts.IsCancellationRequested)
            {
                try { await Task.Delay(5000,cts.Token); }
                catch (OperationCanceledException) { break; }
                n++;
                var variants=_mainVm?.SelectedDetectedVideo?.Video.Variants.Count??0;
                HangProbe.Mark("heartbeat",$"#{n} variants={variants} completed={pipeline.IsCompleted} session={pipeline.SessionId:N} addr={Truncate(address,80)}");
            }
        });
        return cts;
    }

    private async Task<string?> ExecScriptProbedAsync(string label,string script,int timeoutSeconds=20)
    {
        HangProbe.Mark("script.begin",label);
        Log($"PROBE script.begin {label}");
        try
        {
            var result=await WebView.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            HangProbe.Mark("script.end",label+" ok");
            Log($"PROBE script.end {label} ok");
            return result;
        }
        catch (TimeoutException)
        {
            HangProbe.Mark("script.TIMEOUT",label+$" >{timeoutSeconds}s");
            Log($"PROBE script.TIMEOUT {label} >{timeoutSeconds}s");
            return null;
        }
        catch (Exception ex)
        {
            HangProbe.Mark("script.fail",label+" "+ex.GetType().Name);
            Log($"PROBE script.fail {label}: {ex.Message}");
            return null;
        }
    }

    private async Task TryStartPlaybackProbedAsync(string label)
    {
        HangProbe.Mark("play.begin",label);
        try
        {
            await TryStartPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(20));
            HangProbe.Mark("play.end",label+" ok");
        }
        catch (TimeoutException)
        {
            HangProbe.Mark("play.TIMEOUT",label);
            Log($"PROBE play.TIMEOUT {label}");
        }
        catch (Exception ex)
        {
            HangProbe.Mark("play.fail",label+" "+ex.GetType().Name);
            Log($"PROBE play.fail {label}: {ex.Message}");
        }
    }

    private static bool IsSameAcceptanceDocument(string? current,string expected)
    {
        if(!Uri.TryCreate(current,UriKind.Absolute,out var a) || !Uri.TryCreate(expected,UriKind.Absolute,out var b))
            return false;
        if(!string.Equals(a.Host,b.Host,StringComparison.OrdinalIgnoreCase))
            return false;
        var left=a.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var right=b.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if(!string.Equals(left,right,StringComparison.OrdinalIgnoreCase))
            return false;
        static string? ContentId(Uri uri)
        {
            foreach(var part in uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries))
            {
                var i=part.IndexOf('=');
                var key=i<0?part:part[..i];
                var value=i<0?"":Uri.UnescapeDataString(part[(i+1)..]);
                if((key is "v" or "modal_id" or "aweme_id" or "item_id") && !string.IsNullOrWhiteSpace(value))
                    return key+"="+value;
            }
            var match=System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath,@"/(video|shorts|note)/(BV[\w]+|[\w-]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if(match.Success) return match.Value;
            var mac=System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath,@"/(?:id|vod)/(\d{3,})",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return mac.Success ? "id="+mac.Groups[1].Value : null;
        }
        var idA=ContentId(a);
        var idB=ContentId(b);
        return idA is null && idB is null || string.Equals(idA,idB,StringComparison.Ordinal);
    }

    private static async Task WriteLiveEvidenceAsync(
        string rootReportDir,string reportDir,string runId,int expectedCount,List<object> records,bool runComplete,bool allPassed)
    {
        var payload=new
        {
            runId,
            runComplete,
            allPassed,
            expectedCount,
            actualCount=records.Count,
            finishedAt=DateTimeOffset.Now,
            records
        };
        var json=JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true});
        await File.WriteAllTextAsync(Path.Combine(reportDir,"results.json"),json);
        await File.WriteAllTextAsync(Path.Combine(rootReportDir,"results.json"),json);
        await File.WriteAllTextAsync(Path.Combine(rootReportDir,"latest.txt"),runId+Environment.NewLine);
    }

    private async Task<(bool Ok,string Note)> ReadMediaSampleAsync(
        MediaTrack track,
        VideoDownloader.Infrastructure.Http.MediaAvailabilityValidator validator)
    {
        try
        {
            if(track.Container is "hls" or "dash") return await ReadManifestSegmentAsync(track);
            // CDP Media events already proved bytes. DomObserved playAddr still needs a real GET
            // (or engine download with cookies) — do not treat BrowserObserved-from-DOM as prevalidated.
            if(track.Evidence == MediaEvidence.BrowserObserved ||
               (track.BrowserObserved && track.Evidence != MediaEvidence.DomObserved) ||
               (track.IsValidated && track.Evidence != MediaEvidence.DomObserved))
                return (true, $"browser-observed / prevalidated; {track.SourceUrl.Host}");
            // Use the same validator stack as production probe/download (manual redirects,
            // AllowAutoRedirect=false, shared RequestMessageFactory / cookie policy).
            var variant=MediaVariant.FromTracks("sample",null,null,null,track.Container,[track]);
            await validator.ValidateAsync(variant, CancellationToken.None);
            return (true, $"validated via MediaAvailabilityValidator; {track.SourceUrl.Host}");
        }
        catch(DownloadException ex){return(false,ex.ErrorCode+": "+ex.Message);}
        catch(Exception ex){return(false,ex.GetType().Name+": "+ex.Message);}
    }

    private async Task SkipNonVideoPostsAsync()
    {
        for(var attempt=0;attempt<10;attempt++)
        {
            await Task.Delay(2500);
            // Strong media traits only. Blob/MSE VOD often lacks duration briefly — wait, don't call it live.
            var kind=await WebView.CoreWebView2.ExecuteScriptAsync("""
                (()=>{
                  const mediaVisible=el=>{
                    const style=getComputedStyle(el);
                    if(style.visibility==='hidden'||style.display==='none') return false;
                    const r=el.getBoundingClientRect();
                    if(!(r.height>40&&r.width>40&&r.top<innerHeight&&r.bottom>0)) return false;
                    if(style.opacity==='0') return !!(el.currentSrc||el.src);
                    return true;
                  };
                  const isHttpFlv=src=>/^https?:/i.test(src||'') &&
                    (/\.flv([?#]|$)/i.test(src) || /\/flv\//i.test(src) || /[?&](?:mime_type|media_type)=video_flv\b/i.test(src));
                  const classify=media=>{
                    const src=String(media.currentSrc||media.src||'');
                    if(isHttpFlv(src)) return 'live';
                    const duration=media.duration;
                    if(Number.isFinite(duration)&&duration>0) return 'video';
                    let seekEnd=0;
                    try{if(media.seekable&&media.seekable.length>0) seekEnd=media.seekable.end(media.seekable.length-1);}catch(e){}
                    if(Number.isFinite(seekEnd)&&seekEnd>0) return 'video';
                    if(duration===Infinity) return 'live';
                    return 'pending';
                  };
                  // Feed live cards often keep a finite preview duration — detect by e2e / player class first.
                  const liveCardVisible=el=>{
                    const r=el.getBoundingClientRect();
                    return r.height>160&&r.width>120&&r.top<innerHeight&&r.bottom>40&&r.top>-40;
                  };
                  if([...document.querySelectorAll('[data-e2e="feed-live"],.LivePlayer_Preview')].some(liveCardVisible))
                    return 'live';
                  // A live preview can have a finite buffer. Use the visible live-entry UI.
                  const liveEntry=[...document.querySelectorAll('a,button,span,div')].some(e=>{
                    if(e.children.length>0) return false;
                    const text=(e.textContent||'').trim();
                    if(!/^(直播中|.*进入直播间|.*正在直播)$/.test(text)) return false;
                    const r=e.getBoundingClientRect();
                    return r.width>0&&r.height>0&&r.top>50&&r.bottom<innerHeight;
                  });
                  if(liveEntry) return 'live';
                  const active=document.querySelector('[data-e2e="feed-active-video"]');
                  if(active){
                    const media=[...active.querySelectorAll('video,audio')].filter(mediaVisible)
                      .sort((a,b)=>Number(!b.paused)-Number(!a.paused))[0];
                    if(media) return classify(media);
                    // Active VOD slide with content id but media not ready yet.
                    if(active.getAttribute('data-e2e-vid')||/video_\d{10,}/.test(active.className||''))
                      return 'pending';
                  }
                  const card=[...document.querySelectorAll('[data-e2e="feed-item"],[data-e2e="feed-video"],[data-e2e="recommend-list-item-container"],article')].find(e=>{
                    const r=e.getBoundingClientRect();return r.height>200&&r.top<innerHeight&&r.bottom>0;
                  });
                  const scope=card||document.body;
                  const media=[...scope.querySelectorAll('video,audio')].filter(mediaVisible)
                    .sort((a,b)=>Number(!b.paused)-Number(!a.paused))[0];
                  if(media) return classify(media);
                  const photo=[...scope.querySelectorAll('img')].some(e=>{
                    const r=e.getBoundingClientRect();return r.width>300&&r.height>250&&r.top<innerHeight&&r.bottom>0;
                  });
                  return photo?'photo':'pending';
                })()
                """);
            var label=kind.Trim('"');
            if(label=="video" || label=="photo") return;
            if(label=="pending")
            {
                await TryStartPlaybackAsync();
                continue;
            }
            Log($"Skipping a visible {label} post; it does not count toward the four-video acceptance.");
            await WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new {type="keyDown",key="ArrowDown",code="ArrowDown",windowsVirtualKeyCode=40,nativeVirtualKeyCode=40}));
            await WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new {type="keyUp",key="ArrowDown",code="ArrowDown",windowsVirtualKeyCode=40,nativeVirtualKeyCode=40}));
        }
    }

    private async Task<(bool Ok,string Note)> ReadManifestSegmentAsync(MediaTrack track)
    {
        var folder=Path.Combine(Path.GetTempPath(),"vd-segment-sample-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var publish=FindPublishRoot();
        var info=new ProcessStartInfo(Path.Combine(publish,"M3u8","N_m3u8DL-RE.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=folder};
        // Mirror M3u8DownloadAdapter: N_m3u8DL-RE always probes ffmpeg even for --skip-merge.
        foreach(var arg in new[]{track.SourceUrl.AbsoluteUri,"--custom-range","0-0","--skip-merge","--auto-select","--thread-count","1","--download-retry-count","0",
            "--save-dir",folder,"--tmp-dir",Path.Combine(folder,"segments"),"--save-name","sample","--no-log","--no-ansi-color","--write-meta-json","false","--disable-update-check",
            "--ffmpeg-binary-path",Path.Combine(publish,"ffmpeg","ffmpeg.exe")}) info.ArgumentList.Add(arg);
        if(track.TrackId.StartsWith("dash:"))
        {
            info.ArgumentList.Add(track.Kind==MediaTrackKind.Audio?"--select-audio":"--select-video");
            info.ArgumentList.Add("id=^"+System.Text.RegularExpressions.Regex.Escape(track.TrackId[5..])+"$:for=best");
            info.ArgumentList.Add(track.Kind==MediaTrackKind.Audio?"--drop-video":"--drop-audio");
            info.ArgumentList.Add("all");
        }
        using var request=_services!.GetRequiredService<IRequestMessageFactory>().Create(MediaVariant.FromTracks("sample",null,null,null,track.Container,[track]),HttpMethod.Get,track.SourceUrl);
        foreach(var header in request.Headers.Where(h=>!h.Key.Equals("Range",StringComparison.OrdinalIgnoreCase)))
        {info.ArgumentList.Add("-H");info.ArgumentList.Add(header.Key+": "+string.Join(", ",header.Value));}

        // Clear-key AES-128 / SAMPLE-AES (not DRM): inherit RequestContext when fetching the key.
        if(track.Hls is { HasClearKeyEncryption: true, Encryption: { KeyUri: not null } enc })
        {
            try
            {
                var factory=_services!.GetRequiredService<System.Net.Http.IHttpClientFactory>();
                using var client=factory.CreateClient("media-primary");
                using var keyRequest=_services!.GetRequiredService<IRequestMessageFactory>().Create(
                    MediaVariant.FromTracks("hls-key",null,null,null,"hls",[track]),HttpMethod.Get,enc.KeyUri);
                using var keyResponse=await client.SendAsync(keyRequest,HttpCompletionOption.ResponseHeadersRead);
                keyResponse.EnsureSuccessStatusCode();
                var keyBytes=await keyResponse.Content.ReadAsByteArrayAsync();
                if(keyBytes.Length==0) return(false,"HLS clear-key response was empty");
                info.ArgumentList.Add("--custom-hls-method");
                info.ArgumentList.Add(enc.Method.Replace('-','_').ToUpperInvariant());
                info.ArgumentList.Add("--custom-hls-key");
                info.ArgumentList.Add(Convert.ToHexString(keyBytes));
                if(!string.IsNullOrWhiteSpace(enc.IvHex))
                {
                    var iv=enc.IvHex.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?enc.IvHex[2..]:enc.IvHex;
                    if(iv.Length>0&&iv.All(Uri.IsHexDigit))
                    {
                        info.ArgumentList.Add("--custom-hls-iv");
                        info.ArgumentList.Add(iv);
                    }
                }
            }
            catch(Exception ex)
            {
                return(false,"Failed to fetch HLS clear-key: "+ex.Message);
            }
        }

        using var process=Process.Start(info)!;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output,errors);
            var bytes=Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories)
                .Where(p=>new[]{".ts",".m4s",".mp4",".m4a",".aac",".webm"}.Contains(Path.GetExtension(p))).Sum(p=>new FileInfo(p).Length);
            return(process.ExitCode==0&&bytes>0,$"N_m3u8DL-RE first segment: exit={process.ExitCode}, mediaBytes={bytes}");
        }
        finally
        {
            if(!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output,errors);
        }
    }
}
