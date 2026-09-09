using System.Buffers.Binary;

namespace VideoDownloader.Infrastructure.Http;

/// <summary>Checks box boundaries and required metadata before promoting an MP4 part file.
/// This is container validation, not a full decode or a codec compatibility check.</summary>
internal static class Mp4StructureValidator
{
    public static bool IsValid(string path, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        var hasMovie = false;
        var hasData = false;
        while (file.Position < file.Length)
        {
            ct.ThrowIfCancellationRequested();
            var start = file.Position;
            if (file.Length - start < 8) return false;
            file.ReadExactly(header[..8]);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var headerSize = 8;
            if (size == 1)
            {
                if (file.Length - file.Position < 8) return false;
                file.ReadExactly(header[8..]);
                size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0) size = (ulong)(file.Length - start);
            if (size < (ulong)headerSize || size > (ulong)(file.Length - start)) return false;
            if (header.Slice(4, 4).SequenceEqual("moov"u8) && size > (ulong)headerSize)
                hasMovie = true;
            if (header.Slice(4, 4).SequenceEqual("mdat"u8) && size > (ulong)headerSize)
                hasData = true;
            file.Position = start + (long)size;
        }
        return hasMovie && hasData;
    }
}
