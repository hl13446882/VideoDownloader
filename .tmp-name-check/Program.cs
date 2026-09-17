using System;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
class P {
  static void Main() {
    foreach (var t in new[] {
      "第130集：这是后面的剧情简介很长很长很长很长很长很长很长很长很长",
      "第130集:ASCII冒号后面内容很长很长很长很长很长很长很长",
      "第130集：短标题",
      "前缀内容第130集：被夹在中间很长很长很长很长很长很长很长很长"
    }) {
      var video = new DetectedVideo {
        VideoId = "t", SiteId = SiteIds.Generic, PageUrl = new Uri("https://example.com/x"),
        DisplayTitle = t, DetectedAt = DateTimeOffset.UtcNow, DurationSec = 60,
        Variants = new System.Collections.Generic.List<MediaVariant> {
          new MediaVariant { VariantId="1", Label="1080", Height=1080, Container="mp4",
            TotalContentLength=10L*1024*1024, SourceUrl=new Uri("https://example.com/a.mp4") }
        }
      };
      Console.WriteLine("IN : " + t);
      Console.WriteLine("OUT: " + DownloadFileNameBuilder.Build(video, video.Variants[0]));
      Console.WriteLine();
    }
  }
}
