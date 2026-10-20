using System.Security.Cryptography;
using System.Text;

namespace VideoDownloader.Infrastructure.LocalLibrary;

public sealed class VideoEncryptionService
{
    private const string PrivacyKey = "111111";
    // The marker distinguishes fixed-key v2 files from legacy category-key files.
    private static readonly byte[] FileMarker = "VDENC2\r\n"u8.ToArray();
    public const int OriginalHeaderSize = 1024;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("VideoDownloaderPrivacySalt");

    public static int EncryptedHeaderBlockSize
    {
        get
        {
            using var aes = Aes.Create();
            var blockSizeBytes = aes.BlockSize / 8;
            var remainder = OriginalHeaderSize % blockSizeBytes;
            return OriginalHeaderSize + (remainder == 0 ? blockSizeBytes : blockSizeBytes - remainder);
        }
    }

    public static int OverheadBytes => FileMarker.Length + EncryptedHeaderBlockSize - OriginalHeaderSize;

    public static string GenerateObfuscatedFilename(string originalName)
    {
        var ext = Path.GetExtension(originalName);
        var hashInput = originalName + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        var hashName = Convert.ToHexString(hash).ToLowerInvariant()[..16];
        return $"{hashName}{ext}";
    }

    public static async Task EncryptFileAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        var (key, iv) = DeriveKeyAndIv();

        await using var inStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        var headerLen = (int)Math.Min(OriginalHeaderSize, inStream.Length);
        var header = new byte[headerLen];
        await inStream.ReadExactlyAsync(header, ct);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var encryptedHeader = encryptor.TransformFinalBlock(header, 0, header.Length);

        await using var outStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await outStream.WriteAsync(FileMarker, ct);
        await outStream.WriteAsync(encryptedHeader, ct);

        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await inStream.ReadAsync(buffer, ct)) > 0)
            await outStream.WriteAsync(buffer.AsMemory(0, read), ct);
    }

    public static async Task DecryptFileAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        await using var inStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        var encHeaderSize = (int)Math.Min(EncryptedHeaderBlockSize, inStream.Length - FileMarker.Length);
        var marker = new byte[FileMarker.Length];
        await inStream.ReadExactlyAsync(marker, ct);
        if (!marker.AsSpan().SequenceEqual(FileMarker))
            throw new InvalidOperationException("文件未加密或已损坏。");
        if (encHeaderSize < 16 || encHeaderSize % 16 != 0)
            throw new InvalidDataException("加密文件头不完整。");

        var encHeader = new byte[encHeaderSize];
        await inStream.ReadExactlyAsync(encHeader, ct);

        var (key, iv) = DeriveKeyAndIv();
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var decHeader = decryptor.TransformFinalBlock(encHeader, 0, encHeader.Length);

        await using var outStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await outStream.WriteAsync(decHeader, ct);

        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await inStream.ReadAsync(buffer, ct)) > 0)
            await outStream.WriteAsync(buffer.AsMemory(0, read), ct);
    }

    public static DecryptedVideoStream OpenDecryptedRead(string encryptedPath, byte[]? legacyKeyMaterial = null)
    {
        var fs = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, useAsync: true);
        try
        {
            var marker = new byte[FileMarker.Length];
            fs.ReadExactly(marker);
            var currentFormat = marker.AsSpan().SequenceEqual(FileMarker);
            if (!currentFormat && (!marker.AsSpan(0, 5).SequenceEqual("VDENC"u8) || legacyKeyMaterial?.Length != 48))
                throw new InvalidOperationException("文件未加密或已损坏。");
            var markerSize = currentFormat ? FileMarker.Length : 5;
            fs.Position = markerSize;
            var encHeaderSize = (int)Math.Min(EncryptedHeaderBlockSize, fs.Length - markerSize);
            if (encHeaderSize < 16 || encHeaderSize % 16 != 0)
                throw new InvalidDataException("加密文件头不完整。");

            var encHeader = new byte[encHeaderSize];
            fs.ReadExactly(encHeader);

            var (key, iv) = currentFormat ? DeriveKeyAndIv() : (legacyKeyMaterial![..32], legacyKeyMaterial[32..]);
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var decHeader = decryptor.TransformFinalBlock(encHeader, 0, encHeader.Length);

            var bodyOffset = markerSize + encHeaderSize;
            var bodyLength = fs.Length - bodyOffset;
            var virtualLength = decHeader.Length + bodyLength;

            return new DecryptedVideoStream(fs, decHeader, bodyOffset, virtualLength);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static bool IsEncryptedFile(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Recognize old encrypted files too, so they cannot accidentally be encrypted twice.
            var marker = new byte[5];
            if (fs.Read(marker, 0, marker.Length) < marker.Length)
                return false;
            return marker.AsSpan().SequenceEqual("VDENC"u8);
        }
        catch
        {
            return false;
        }
    }

    internal static byte[] DeriveLegacyKeyMaterial(string password) => Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(password), Salt, iterations: 10000, HashAlgorithmName.SHA256, 48);

    private static (byte[] Key, byte[] IV) DeriveKeyAndIv()
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(PrivacyKey), Salt,
            iterations: 10000, HashAlgorithmName.SHA256, 48);
        return (derived[..32], derived[32..]);
    }
}

public sealed class DecryptedVideoStream : Stream
{
    private readonly FileStream _fileStream;
    private readonly byte[] _decryptedHeader;
    private readonly long _bodyFileOffset;
    private readonly long _virtualLength;
    private long _position;

    internal DecryptedVideoStream(FileStream fileStream, byte[] decryptedHeader, long bodyFileOffset, long virtualLength)
    {
        _fileStream = fileStream;
        _decryptedHeader = decryptedHeader;
        _bodyFileOffset = bodyFileOffset;
        _virtualLength = virtualLength;
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _virtualLength;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _virtualLength);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _virtualLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        _position = Math.Clamp(newPos, 0, _virtualLength);
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _virtualLength)
            return 0;

        var headerLen = _decryptedHeader.Length;
        var available = (int)Math.Min(buffer.Length, _virtualLength - _position);
        var bytesRead = 0;

        if (_position < headerLen)
        {
            var fromHeader = (int)Math.Min(available, headerLen - _position);
            _decryptedHeader.AsSpan((int)_position, fromHeader).CopyTo(buffer);
            _position += fromHeader;
            available -= fromHeader;
            bytesRead += fromHeader;
        }

        if (available > 0 && _position >= headerLen)
        {
            var bodyVirtualPos = _position - headerLen;
            var filePos = _bodyFileOffset + bodyVirtualPos;
            _fileStream.Seek(filePos, SeekOrigin.Begin);
            var read = _fileStream.Read(buffer.Slice(bytesRead, available));
            _position += read;
            bytesRead += read;
        }

        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await ReadAsync(buffer.AsMemory(offset, count), ct);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= _virtualLength)
            return 0;

        var headerLen = _decryptedHeader.Length;
        var available = (int)Math.Min(buffer.Length, _virtualLength - _position);
        var bytesRead = 0;

        if (_position < headerLen)
        {
            var fromHeader = (int)Math.Min(available, headerLen - _position);
            _decryptedHeader.AsSpan((int)_position, fromHeader).CopyTo(buffer.Span);
            _position += fromHeader;
            available -= fromHeader;
            bytesRead += fromHeader;
        }

        if (available > 0 && _position >= headerLen)
        {
            var bodyVirtualPos = _position - headerLen;
            var filePos = _bodyFileOffset + bodyVirtualPos;
            _fileStream.Seek(filePos, SeekOrigin.Begin);
            var read = await _fileStream.ReadAsync(buffer.Slice(bytesRead, available), ct);
            _position += read;
            bytesRead += read;
        }

        return bytesRead;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _fileStream.Dispose();
        base.Dispose(disposing);
    }
}
