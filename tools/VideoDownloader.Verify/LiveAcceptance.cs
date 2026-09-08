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
        Log($"Live acceptance runId={runId}; evidence={reportDir}");
        var records=new List<object>();
        var identities=new HashSet<string>(StringComparer.Ordinal);
        var allPassed=true;
        var ordinal=0;
        var expectedCount=urls.Sum(address=>{
            var uri=new Uri(address);
            return uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase)
                || (uri.Host.Contains("douyin",StringComparison.OrdinalIgnoreCase)&&uri.AbsolutePath is "/" or "")
                ? 4 : 1;
        });
        try
        {
            foreach(var address in urls)
            {
                var uri=new Uri(address);
                var feed=uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase)
                    || (uri.Host.Contains("douyin",StringComparison.OrdinalIgnoreCase)&&uri.AbsolutePath is "/" or "");
                await EnsureDocumentNavigatedAsync(pipeline,address);
                // Generic MacCMS pages often show a notice modal that blocks the player iframe.
                if(!feed)
                {
                    await WebView.CoreWebView2.ExecuteScriptAsync("""
                        (()=>{for(const t of['我知道了','关闭','同意','进入']){
                          const a=[...document.querySelectorAll('a,button')].find(e=>e.textContent.trim()===t);
                          if(a){a.click();return t;}
                        }return null;})()
                        """);
                    await Task.Delay(2500);
                    await TryStartPlaybackAsync();
                    // Give parse iframes time to request m3u8.
                    for(var w=0;w<8;w++)
                    {
                        await Task.Delay(1500);
                        await TryStartPlaybackAsync();
                        if(_mainVm.SelectedDetectedVideo?.Video.Variants.Count>0) break;
                    }
                }
                if(feed && uri.Host.Contains("douyin",StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(2500);
                    Log("Recommendation navigation="+await WebView.CoreWebView2.ExecuteScriptAsync("(()=>{const a=[...document.querySelectorAll('a,button,[role=link]')].find(e=>e.textContent.trim()==='推荐');if(a){a.click();return 'clicked 推荐';}return location.href;})()"));
                    await Task.Delay(2500);
                }
                if(feed && uri.Host.Contains("tiktok",StringComparison.OrdinalIgnoreCase)) await SkipNonVideoPostsAsync();
                for(var step=0;step<(feed?4:1);step++)
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
                    var deadline=DateTime.UtcNow.AddSeconds(180);
                    while(DateTime.UtcNow<deadline)
                    {
                        await TryStartPlaybackAsync();
                        var changed=step==0 || pipeline.SessionId!=previousSession;
                        var hasMedia=_mainVm.SelectedDetectedVideo?.Video.Variants.Count>0;
                        if(changed && pipeline.IsCompleted && !string.IsNullOrWhiteSpace(_mainVm.SelectedTab.Host.CurrentMediaSessionKey) &&
                           (hasMedia || DateTime.UtcNow>deadline-TimeSpan.FromSeconds(8))) break;
                        await Task.Delay(1500);
                    }
                    var video=_mainVm.SelectedDetectedVideo?.Video;
                    var completed=pipeline.IsCompleted;
                    var identity=_mainVm.SelectedTab.Host.CurrentMediaSessionKey;
                    var session=pipeline.SessionId;
                    var finalPage=_mainVm.SelectedTab.Host.CurrentPageUrl?.AbsoluteUri ?? address;
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
                                    result.Note.Contains("NET_TIMEOUT",StringComparison.OrdinalIgnoreCase))
                            {
                                // Browser already played these bytes; 2MiB proof is the hard gate.
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
                                else
                                    sampleOk &= false;
                            }
                            else
                            {
                                // Transient CDN timeout: try another progressive track before failing.
                                var alt=MediaVariantRanking.Rank(video.Variants)
                                    .SelectMany(v=>v.Tracks)
                                    .Where(t=>t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
                                    .Where(t=>t.SourceUrl!=track.SourceUrl)
                                    .Where(t=>!UnifiedMediaPipeline.IsDouyinPlayGateway(t.SourceUrl))
                                    .FirstOrDefault();
                                if(alt is not null &&
                                   result.Note.Contains("NET_TIMEOUT",StringComparison.OrdinalIgnoreCase))
                                {
                                    var altResult=await ReadMediaSampleAsync(alt,validator);
                                    samples.Add($"retry {alt.Kind}: {altResult.Note}");
                                    if(altResult.Ok) anyTrackOk=true;
                                    else sampleOk &= false;
                                }
                                else
                                    sampleOk &= result.Ok;
                            }
                        }
                        if(albumOk || (selected is not null && MediaVariantReconciler.HasCompleteAudio(selected)))
                            sampleOk &= anyTrackOk || tracks.All(t=>t.Kind==MediaTrackKind.Image);

                        // Product gate: ≥20 MiB downloaded, or the shorter object finishes completely.
                        if(sampleOk && selected is not null)
                        {
                            const long proofMin=20L*1024*1024;
                            var proofCandidates=albumOk
                                ? selected.Tracks.Where(t=>t.Kind is MediaTrackKind.Audio or MediaTrackKind.Combined).Take(1).ToArray()
                                : selected.Tracks
                                    .Where(t=>t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
                                    .OrderBy(t=>UnifiedMediaPipeline.IsDouyinPlayGateway(t.SourceUrl)?1:0)
                                    .Concat((video.Variants??[]).SelectMany(v=>v.Tracks)
                                        .Where(t=>t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
                                        .OrderBy(t=>UnifiedMediaPipeline.IsDouyinPlayGateway(t.SourceUrl)?1:0))
                                    .DistinctBy(t=>t.SourceUrl.AbsoluteUri)
                                    .ToArray();
                            var proofOk=false;
                            string? proofNote=null;
                            foreach(var proofTrack in proofCandidates)
                            {
                                var proof=await ProveDownloadAtLeastAsync(proofTrack,proofMin);
                                proofNote=proof.Note;
                                if(proof.Ok){proofOk=true;break;}
                            }
                            samples.Add($"download≥20MiB-or-complete: {proofNote??"no track"}");
                            sampleOk &= proofOk;
                        }
                    }
                    // Short hold only: autoplay feeds advance during multi-second waits and falsely fail stability.
                    await Task.Delay(1200);
                    var stable=pipeline.SessionId==session && switches<=1;
                    // step 0: settled first video after document navigation.
                    // later feed steps: new session+identity vs prior video, with no settle-phase thrash.
                    var switched=step==0
                        ? (!string.IsNullOrWhiteSpace(identity) || sampleOk)
                        : session!=previousSession && !string.Equals(identity,previousIdentity,StringComparison.Ordinal) && switches<=1
                          || (feed && sampleOk && session!=previousSession && switches<=1);
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
                    var pass=completed && sampleOk && stable && switched && captionOk && uniqueIdentity;
                    var diagnostic=pass ? null : await WebView.CoreWebView2.ExecuteScriptAsync("(()=>{let e=[...document.querySelectorAll('video')].find(e=>{let r=e.getBoundingClientRect();return r.bottom>0&&r.top<innerHeight;});const rows=[];for(let i=0;e&&i<14;i++,e=e.parentElement){const k=Object.keys(e).find(k=>k.startsWith('__reactProps$'));const p=k?e[k]:{};rows.push({tag:e.tagName,attrs:[...e.attributes].map(a=>[a.name,a.value]),props:Object.keys(p||{}),itemKeys:Object.keys(p?.item||p?.itemInfo||p?.data||{}),src:e.currentSrc});}return JSON.stringify(rows);})()");
                    allPassed &= pass;
                    _mainVm.SelectedTab.Host.MediaSessionChanged-=Switched;
                    await using(var image=File.Create(Path.Combine(reportDir,$"{ordinal:00}.png")))
                        await WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
                    var record=new {
                        runId,ordinal,address,finalPage,step=step+1,pass,completed,stable,switched,switches,captionOk,uniqueIdentity,
                        identity,session,title=video?.DisplayTitle,captionSource=video is null?null:"player-or-probe",
                        heights=video?.Variants.Select(v=>v.Height).Distinct().ToArray(),
                        variants=video?.Variants.Select(v=>new{v.Height,v.VariantId,v.Container,tracks=v.Tracks.Select(t=>new{t.Kind,t.TrackId,t.SourceUrl})}),
                        album=video?.Variants.Any(v=>string.Equals(v.Container,"album",StringComparison.OrdinalIgnoreCase)||v.Tracks.Any(t=>t.Kind==MediaTrackKind.Image)),samples,
                        status=_mainVm.StatusMessage,externalError=(pipeline as UnifiedMediaPipeline)?.LastExternalError,
                        validationError=(pipeline as UnifiedMediaPipeline)?.LastValidationError,dom=snapshot,diagnostic};
                    records.Add(record);
                    await WriteLiveEvidenceAsync(rootReportDir,reportDir,runId,expectedCount,records,runComplete:false,allPassed);
                    Log($"LIVE {(pass?"PASS":"FAIL")} {uri.Host} #{step+1}: completed={completed} stable={stable} switched={switched}/{switches} caption={captionOk} unique={uniqueIdentity} {video?.DisplayTitle}; {string.Join("; ",samples)}");
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
            if(track.BrowserObserved || track.IsValidated)
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

    private async Task<(bool Ok,string Note)> ProveDownloadAtLeastAsync(MediaTrack track,long minBytes)
    {
        try
        {
            if(track.Container is "hls" or "dash" ||
               track.SourceUrl.AbsolutePath.EndsWith(".m3u8",StringComparison.OrdinalIgnoreCase) ||
               track.SourceUrl.AbsolutePath.EndsWith(".mpd",StringComparison.OrdinalIgnoreCase) ||
               UnifiedMediaPipeline.IsDouyinLiveStream(track.SourceUrl))
                return await ProveManifestDownloadAtLeastAsync(track,minBytes);

            using var client=_services!.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("media-primary");
            var factory=_services!.GetRequiredService<IRequestMessageFactory>();
            var url=track.SourceUrl;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(300));
            for(var hop=0;hop<6;hop++)
            {
                using var request=factory.Create(
                    MediaVariant.FromTracks("proof",null,null,null,track.Container,[track with { SourceUrl=url }]),HttpMethod.Get,url);
                request.Headers.Remove("Range");
                request.Headers.Remove("If-Range");
                request.Headers.Range=new System.Net.Http.Headers.RangeHeaderValue(0,minBytes-1);
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if((int)response.StatusCode is >=300 and <400)
                {
                    var location=response.Headers.Location;
                    if(location is null) return(false,$"HTTP {(int)response.StatusCode} without Location from {url.Host}");
                    url=location.IsAbsoluteUri ? location : new Uri(url,location);
                    continue;
                }
                if(!response.IsSuccessStatusCode)
                    return(false,$"HTTP {(int)response.StatusCode} from {url.Host}");
                await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer=new byte[256*1024];
                long total=0;
                while(total<minBytes)
                {
                    var read=await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,minBytes-total)),timeout.Token);
                    if(read==0) break;
                    total+=read;
                }
                // Short VOD under 20MiB still counts when the body finished (≥64KiB).
                var entityLength=TryGetEntityLength(response) ?? response.Content.Headers.ContentLength;
                var complete=entityLength is long el && el>0 && el<minBytes && total>=el && total>=64*1024;
                var ok=total>=minBytes || complete;
                return(ok,$"bytes={total} entity={entityLength?.ToString()??"?"} complete={complete} host={url.Host}");
            }
            return(false,$"too many redirects from {track.SourceUrl.Host}");
        }
        catch(Exception ex)
        {
            return(false,ex.GetType().Name+": "+ex.Message);
        }
    }

    private static long? TryGetEntityLength(HttpResponseMessage response)
    {
        if(response.Content.Headers.ContentRange?.Length is long ranged)
            return ranged;
        if(response.Headers.TryGetValues("Content-Range",out var values))
        {
            var raw=values.FirstOrDefault()??"";
            var slash=raw.LastIndexOf('/');
            if(slash>=0 && long.TryParse(raw[(slash+1)..],out var total) && total>0)
                return total;
        }
        return response.Content.Headers.ContentLength;
    }

    private async Task<(bool Ok,string Note)> ProveManifestDownloadAtLeastAsync(MediaTrack track,long minBytes)
    {
        var folder=Path.Combine(Path.GetTempPath(),"vd-proof-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var publish=FindPublishRoot();
        // Pull enough segments to reach ≥20MiB (or finish a short VOD playlist).
        var info=new ProcessStartInfo(Path.Combine(publish,"M3u8","N_m3u8DL-RE.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=folder};
        foreach(var arg in new[]{track.SourceUrl.AbsoluteUri,"--custom-range","0-800","--skip-merge","--auto-select","--thread-count","8","--download-retry-count","2",
            "--save-dir",folder,"--tmp-dir",Path.Combine(folder,"segments"),"--save-name","proof","--no-log","--no-ansi-color","--write-meta-json","false","--disable-update-check",
            "--ffmpeg-binary-path",Path.Combine(publish,"ffmpeg","ffmpeg.exe")}) info.ArgumentList.Add(arg);
        using var request=_services!.GetRequiredService<IRequestMessageFactory>().Create(MediaVariant.FromTracks("proof",null,null,null,track.Container,[track]),HttpMethod.Get,track.SourceUrl);
        foreach(var header in request.Headers.Where(h=>!h.Key.Equals("Range",StringComparison.OrdinalIgnoreCase)))
        {info.ArgumentList.Add("-H");info.ArgumentList.Add(header.Key+": "+string.Join(", ",header.Value));}
        using var process=Process.Start(info)!;
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        var deadline=DateTime.UtcNow.AddSeconds(600);
        try
        {
            while(DateTime.UtcNow<deadline)
            {
                var bytes=SumMediaBytes(folder);
                if(bytes>=minBytes)
                {
                    try{process.Kill(true);}catch{/* best-effort */}
                    return(true,$"N_m3u8DL-RE proof: early-stop mediaBytes={bytes}");
                }
                if(process.HasExited)
                {
                    await Task.WhenAll(stdout,stderr);
                    // Exit 0 with substantial bytes under the cap = short playlist finished completely.
                    var complete=process.ExitCode==0 && bytes>=64*1024 && bytes<minBytes;
                    var ok=(process.ExitCode==0&&bytes>=minBytes) || complete;
                    return(ok,$"N_m3u8DL-RE proof: exit={process.ExitCode}, mediaBytes={bytes}, complete={complete}");
                }
                await Task.Delay(1500);
            }
            var partial=SumMediaBytes(folder);
            try{process.Kill(true);}catch{/* best-effort */}
            return(false,$"N_m3u8DL-RE proof: timeout mediaBytes={partial}");
        }
        finally
        {
            if(!process.HasExited) try{process.Kill(true);}catch{/* best-effort */}
            try{Directory.Delete(folder,true);}catch{/* best-effort */}
        }
    }

    private static long SumMediaBytes(string folder)
    {
        if(!Directory.Exists(folder)) return 0;
        try
        {
            return Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories)
                .Where(p=>new[]{".ts",".m4s",".mp4",".m4a",".aac",".webm",".mkv"}.Contains(Path.GetExtension(p)))
                .Sum(p=>{try{return new FileInfo(p).Length;}catch{return 0L;}});
        }
        catch { return 0; }
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
