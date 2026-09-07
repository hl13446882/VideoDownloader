using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;

namespace VideoDownloader.Infrastructure.Configuration;

public sealed class AppSiteEnablementPolicy : ISiteEnablementPolicy
{
    private readonly AppOptions _options;

    public AppSiteEnablementPolicy(IOptions<AppOptions> options) => _options = options.Value;

    public bool PreferSiteAdapters => _options.Sites.PreferSiteAdapters;

    public bool FallbackToGeneric => _options.Sites.FallbackToGeneric;

    public bool IsSiteEnabled(string siteId) => _options.Sites.GetSiteEnabled(siteId);
}
