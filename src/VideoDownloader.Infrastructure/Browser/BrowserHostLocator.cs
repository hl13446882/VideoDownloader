namespace VideoDownloader.Infrastructure.Browser;

public sealed class BrowserHostLocator
{
    private readonly object _sync = new();
    private WebView2Host? _active;

    public WebView2Host? Active
    {
        get
        {
            lock (_sync)
                return _active;
        }
        set
        {
            lock (_sync)
                _active = value;
        }
    }
}
