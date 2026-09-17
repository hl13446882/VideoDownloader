using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
var titles = new[] {
  ""第130集：这是后面的剧情简介很长很长很长很长很长很长很长"",
  ""第130集:这是后面的剧情简介"",
  ""第130集：短"",
};
foreach (var t in titles) {
  var v = new DetectedVideo {
    VideoId = ""t"", SiteId = SiteIds.Generic, PageUrl = new Uri(""https://example.com/x""),
    DisplayTitle = t, DetectedAt = DateTimeOffset.UtcNow,
    Variants = [ new MediaVariant { VariantId=""1"", Label=""1080"", Height=1080, Container=""mp4"", TotalContentLength=10*1024*1024, SourceUrl=new Uri(""https://example.com/a.mp4"") } ],
    DurationSec = 60
  };
  Console.WriteLine(""IN : "" + t);
  Console.WriteLine(""OUT: "" + DownloadFileNameBuilder.Build(v, v.Variants[0]));
  Console.WriteLine();
}
