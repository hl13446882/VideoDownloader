using System;
using System.Globalization;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
class P {
  static void Main() {
    foreach (var t in new[] {
      "第130集：这是后面的剧情简介很长很长很长很长很长很长很长很长很长很长",
      "第130集:ASCII冒号后面内容很长很长很长很长很长很长很长很长很长",
      "第130集：短标题"
    }) {
      var video = new DetectedVideo(
        Guid.NewGuid(), SiteIds.Generic, null, t, new Uri("https://example.com/x"),
        MediaFamily.DirectMp4,
        [MediaVariant.FromCombinedTrack("1080p", new Uri("https://cdn.example.test/v.mp4"), RequestContext.CreateEmpty(), height:1080, container:"mp4", contentLength:10L*1024*1024)],
        false) { DurationSec = 60 };
      var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);
      Console.WriteLine("IN : " + t);
      Console.WriteLine("OUT: " + name);
      DownloadFileNameBuilder.TrySplitMetaSuffix(name, out var head, out _);
      Console.WriteLine("HEAD len=" + new StringInfo(head).LengthInTextElements + " head=" + head);
      Console.WriteLine();
    }
  }
}
