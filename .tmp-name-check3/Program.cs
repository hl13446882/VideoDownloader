using System;
using System.IO;
using System.Text;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;

var titles = new[] {
  "@笑里藏叨第174集：《119令》 4#社会语录 #葫芦相声社 #葫芦相声",
  "@笑里藏叨 第174集：《119令》 4#社会语录 #葫芦相声社 #葫芦相声",
  "第174集：《119令》 4#社会语录 #葫芦相声社 #葫芦相声",
};
var sb = new StringBuilder();
foreach (var title in titles)
{
  var video = new DetectedVideo(
      Guid.NewGuid(), "douyin", "x", title,
      new Uri("https://www.douyin.com/video/1"),
      MediaFamily.DirectMp4,
      [MediaVariant.FromCombinedTrack("1080p", new Uri("https://example.com/a.mp4"), RequestContext.CreateEmpty(), null, 1080, null, null, "mp4", 12L*1024*1024)],
      false);
  var name = DownloadFileNameBuilder.Build(video, video.Variants[0]);
  sb.AppendLine("IN=[" + title + "]");
  sb.AppendLine("OUT=[" + name + "]");
}
File.WriteAllText(@"D:\VideoDownloader\.tmp-name-check3\out2.txt", sb.ToString(), new UTF8Encoding(true));