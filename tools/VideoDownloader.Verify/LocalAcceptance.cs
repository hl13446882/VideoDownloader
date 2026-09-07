using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Verify;

public partial class MainWindow
{
    private async Task<bool> RunLocalAcceptanceAsync()
    {
        var root = Path.Combine(Path.GetTempPath(),"vd-acceptance-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sample = Path.Combine(root,"sample.mp4");
        var psi = new ProcessStartInfo(Path.Combine(FindPublishRoot(),"ffmpeg","ffmpeg.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-v","error","-f","lavfi","-i","testsrc2=size=1280x720:rate=24","-f","lavfi","-i","sine=frequency=440","-t","4","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac",sample}) psi.ArgumentList.Add(arg);
        using(var generator=Process.Start(psi)!)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var errors=generator.StandardError.ReadToEndAsync(timeout.Token);
            await generator.WaitForExitAsync(timeout.Token);
            if(generator.ExitCode!=0) throw new InvalidOperationException(await errors);
        }
        var bytes=await File.ReadAllBytesAsync(sample);
        using var tcp=new TcpListener(IPAddress.Loopback,0);
        tcp.Start(); var port=((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        using var listener=new HttpListener();
        var origin=$"http://localhost:{port}";
        listener.Prefixes.Add(origin+"/"); listener.Start();
        var html="""
            <!doctype html><title>Wrong page title</title><figure data-video-id="A">
            <video muted autoplay loop width="640" height="360" src="/A.mp4" style="opacity:0"></video>
            <figcaption>Actual caption A</figcaption></figure>
            <script>window.selectVideo=(id)=>{document.querySelector('figure').dataset.videoId=id;
            document.querySelector('figcaption').textContent='Actual caption '+id;
            const v=document.querySelector('video');v.src='/'+id+'.mp4';v.play().catch(()=>{});};</script>
            """;
        var server=Task.Run(async()=>
        {
            try
            {
                while(listener.IsListening)
                {
                    var request=await listener.GetContextAsync();
                    _=Task.Run(async()=>
                    {
                        try
                        {
                            var media=request.Request.Url!.AbsolutePath.EndsWith(".mp4");
                            var payload=media ? bytes : Encoding.UTF8.GetBytes(html);
                            long start=0,end=payload.Length-1;
                            if(media && request.Request.Headers["Range"] is { } range)
                            {
                                var fields=range[6..].Split('-');
                                start=long.Parse(fields[0]);
                                if(fields.Length>1 && fields[1].Length>0) end=Math.Min(end,long.Parse(fields[1]));
                                request.Response.StatusCode=206;
                                request.Response.Headers["Content-Range"]=$"bytes {start}-{end}/{payload.Length}";
                            }
                            request.Response.ContentType=media ? "video/mp4":"text/html; charset=utf-8";
                            request.Response.ContentLength64=end-start+1;
                            request.Response.Headers["Accept-Ranges"]="bytes";
                            if(request.Request.HttpMethod!="HEAD") await request.Response.OutputStream.WriteAsync(payload.AsMemory((int)start,(int)(end-start+1)));
                        }
                        catch(Exception) { }
                        finally {request.Response.Close();}
                    });
                }
            }
            catch(HttpListenerException) { }
            catch(ObjectDisposedException) { }
        });
        try
        {
            Require(await RunAsync([origin+"/feed"]),"Initial real WebView detection");
            var pipeline=_services!.GetRequiredService<IMediaDetectionPipeline>();
            await WaitUntilAsync(()=>_mainVm!.StatusMessage.Contains("完成"),TimeSpan.FromSeconds(35));
            var first=pipeline.SessionId;
            Log("Initial anchor="+_mainVm!.SelectedTab!.Host.CurrentMediaSessionKey);
            Require(!string.IsNullOrWhiteSpace(_mainVm.SelectedTab.Host.CurrentMediaSessionKey),"Observer installed at document creation and confirmed initial identity");
            pipeline.VideoDetected+=(_,v)=>Log($"DETECTED session={v.SessionId} title={v.DisplayTitle}");
            Require(_mainVm!.SelectedDetectedVideo?.Video.DisplayTitle=="Actual caption A","A4 active-player caption");
            var firstUrl=_mainVm.SelectedDetectedVideo!.Video.Variants[0].SourceUrl;
            await WebView.CoreWebView2.ExecuteScriptAsync("document.querySelector('video').src='/A-high.mp4?token=new';document.querySelector('video').play();document.title='Still wrong';");
            await Task.Delay(4000);
            Require(pipeline.SessionId==first,"A2 quality/CDN-token observation preserves session");
            Require(_mainVm.SelectedDetectedVideo!.Video.Variants.Any(v=>v.SourceUrl==firstUrl),"A2 discovered list retained");
            var switches=0;
            _mainVm.SelectedTab!.Host.MediaSessionChanged+=(_,e)=> { switches++;Log("SWITCH "+e.MediaSessionKey); };
            await WebView.CoreWebView2.ExecuteScriptAsync("selectVideo('B')");
            Log("DOM after switch="+await WebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__vdObserve?.())"));
            await Task.Delay(4000);
            Log($"After switch: session={pipeline.SessionId} anchor={_mainVm.SelectedTab.Host.CurrentMediaSessionKey} status={_mainVm.StatusMessage} title={_mainVm.SelectedDetectedVideo?.Video.DisplayTitle}");
            await WaitUntilAsync(()=>pipeline.SessionId!=first && _mainVm.SelectedDetectedVideo?.Video.DisplayTitle=="Actual caption B",TimeSpan.FromSeconds(35));
            var second=pipeline.SessionId;
            await Task.Delay(4000);
            Require(switches==1 && pipeline.SessionId==second,"A1 same-URL switch creates exactly one session");
            Require(_mainVm.SelectedDetectedVideo!.Video.Variants.All(v=>!v.SourceUrl.AbsolutePath.StartsWith("/A")),"A3 old video absent from new list");
            var engine=_services!.GetRequiredService<IDownloadEngine>();
            foreach(var variant in _mainVm.SelectedDetectedVideo.Video.Variants)
            {
                var previous=engine.GetActiveJobs().Select(j=>j.Id).ToHashSet();
                await engine.EnqueueAsync(variant,"Acceptance "+Guid.NewGuid().ToString("N")[..6],new Uri(origin+"/feed"));
                var job=engine.GetActiveJobs().Single(j=>!previous.Contains(j.Id));
                await WaitUntilAsync(()=>job.Status is DownloadStatus.Completed or DownloadStatus.Failed,TimeSpan.FromSeconds(60));
                Require(job.Status==DownloadStatus.Completed && File.Exists(job.TargetPath) && job.DownloadedBytes==new FileInfo(job.TargetPath).Length,
                    "Full download and byte accounting: "+variant.VariantId+" "+job.LastErrorCode);
                var probe=new ProcessStartInfo(Path.Combine(FindPublishRoot(),"ffmpeg","ffprobe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{"-v","error","-show_entries","stream=codec_type","-of","json",job.TargetPath})probe.ArgumentList.Add(arg);
                using var p=Process.Start(probe)!;
                var output=p.StandardOutput.ReadToEndAsync();var errors=p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();await errors;
                using var streams=System.Text.Json.JsonDocument.Parse(await output);
                var kinds=streams.RootElement.GetProperty("streams").EnumerateArray().Select(x=>x.GetProperty("codec_type").GetString()).ToArray();
                Require(p.ExitCode==0 && kinds.Contains("audio") && (variant.Tracks.All(t=>t.Kind==MediaTrackKind.Audio) ? !kinds.Contains("video") : kinds.Contains("video")),"Output stream kinds match selected mode");
                await engine.RemoveAsync(job.Id);
                Require(File.Exists(job.TargetPath),"Remove history row preserves file");
            }
            Log("LOCAL ACCEPTANCE PASS: real WebView2 + MainViewModel + download backends; A5 deferred tasks are covered by business tests.");
            return true;
        }
        finally
        {
            if(_mainVm is not null) await _mainVm.DisposeHostsAsync();
            listener.Stop();await server;
        }
    }

    private void Require(bool passed,string description)
    {
        Log((passed?"PASS: ":"FAIL: ")+description);
        if(!passed) throw new InvalidOperationException(description);
    }
    private static async Task WaitUntilAsync(Func<bool> condition,TimeSpan timeout)
    {
        var deadline=DateTime.UtcNow+timeout;
        while(!condition())
        {
            if(DateTime.UtcNow>deadline) throw new TimeoutException("Acceptance condition timed out.");
            await Task.Delay(200);
        }
    }
}
