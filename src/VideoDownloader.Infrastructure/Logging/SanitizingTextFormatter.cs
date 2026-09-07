using Serilog.Events;
using Serilog.Formatting;

namespace VideoDownloader.Infrastructure.Logging;

public sealed class SanitizingTextFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => "VRB",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Fatal => "FTL",
            _ => "UNK"
        };

        var message = SanitizedLogger.SanitizeMessage(logEvent.RenderMessage());
        output.Write($"{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");

        if (logEvent.Exception is not null)
        {
            output.WriteLine();
            output.Write(SanitizedLogger.SanitizeMessage(logEvent.Exception.ToString()));
        }

        output.WriteLine();
    }
}
