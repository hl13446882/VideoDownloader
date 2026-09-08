namespace VideoDownloader.Core.Models;

/// <summary>
/// Clear-key HLS encryption payload for the download path only.
/// Attached solely when METHOD is AES-128 / SAMPLE-AES without license DRM.
/// </summary>
public sealed record HlsEncryption(
    string Method,
    Uri? KeyUri,
    string? IvHex);

public sealed record HlsMedia(
    Uri PlaylistUrl,
    IReadOnlyList<Uri> Segments,
    HlsEncryption? Encryption,
    long MediaSequence,
    RequestContext RequestContext)
{
    /// <summary>
    /// True only for clear-key AES that our downloader can decrypt (not Widevine/PlayReady/etc.).
    /// </summary>
    public bool HasClearKeyEncryption =>
        Encryption is { KeyUri: not null } enc &&
        IsClearKeyMethod(enc.Method);

    public static bool IsClearKeyMethod(string? method) =>
        method is not null &&
        (method.Equals("AES-128", StringComparison.OrdinalIgnoreCase) ||
         method.Equals("SAMPLE-AES", StringComparison.OrdinalIgnoreCase));
}
