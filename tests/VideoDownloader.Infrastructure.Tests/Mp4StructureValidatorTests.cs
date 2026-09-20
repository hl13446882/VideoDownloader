using System.Buffers.Binary;
using VideoDownloader.Infrastructure.Http;

namespace VideoDownloader.Infrastructure.Tests;

public class Mp4StructureValidatorTests
{
    [Fact]
    public void IsValid_AcceptsProgressiveMoovMdat()
    {
        var path = Path.Combine(Path.GetTempPath(), "vd-mp4-prog-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            WriteBoxes(path, ("ftyp", 16), ("moov", 32), ("mdat", 64));
            Assert.True(Mp4StructureValidator.IsValid(path, CancellationToken.None));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void IsValid_AcceptsFragmentedMoofMdat()
    {
        var path = Path.Combine(Path.GetTempPath(), "vd-mp4-frag-" + Guid.NewGuid().ToString("N") + ".m4s");
        try
        {
            WriteBoxes(path, ("styp", 16), ("moof", 48), ("mdat", 128));
            Assert.True(Mp4StructureValidator.IsValid(path, CancellationToken.None));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void IsValid_RejectsMdatWithoutMovieOrFragment()
    {
        var path = Path.Combine(Path.GetTempPath(), "vd-mp4-bad-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            WriteBoxes(path, ("ftyp", 16), ("mdat", 64));
            Assert.False(Mp4StructureValidator.IsValid(path, CancellationToken.None));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void WriteBoxes(string path, params (string Type, int Payload)[] boxes)
    {
        using var fs = File.Create(path);
        Span<byte> header = stackalloc byte[8];
        foreach (var (type, payload) in boxes)
        {
            var size = 8 + payload;
            BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)size);
            System.Text.Encoding.ASCII.GetBytes(type.AsSpan(0, 4), header[4..8]);
            fs.Write(header);
            if (payload > 0)
                fs.Write(new byte[payload]);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
