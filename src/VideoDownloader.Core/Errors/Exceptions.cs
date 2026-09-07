namespace VideoDownloader.Core.Errors;

public sealed class RangeMismatchException : Exception
{
    public RangeMismatchException() : base("Range or entity version mismatch.") { }
}

public sealed class DownloadException : Exception
{
    public string ErrorCode { get; }

    public DownloadException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
