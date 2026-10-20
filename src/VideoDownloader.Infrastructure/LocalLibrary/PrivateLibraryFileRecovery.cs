using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.LocalLibrary;

internal static class PrivateLibraryFileRecovery
{
    internal static string? Find(DownloadJob job, IReadOnlyList<DownloadJob> jobs)
    {
        if (File.Exists(job.TargetPath)) return null;
        var directory = Path.GetDirectoryName(job.TargetPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
        var size = job.DownloadedBytes > 0 ? job.DownloadedBytes : job.TotalBytes ?? job.ExpectedTotalBytes ?? 0;
        if (size <= 0) return null;
        var claimed = jobs.Where(j => j.Id != job.Id).Select(j => Path.GetFullPath(j.TargetPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = Directory.EnumerateFiles(directory)
            .Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(p), "^[0-9a-f]{16}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(p => Path.GetExtension(p).Equals(Path.GetExtension(job.TargetPath), StringComparison.OrdinalIgnoreCase))
            .Where(p => !claimed.Contains(Path.GetFullPath(p)))
            .Where(p => Matches(p, size)).Take(2).ToArray();
        if (candidates.Length != 1) return null;
        // Size alone cannot distinguish multiple orphaned records with identical media sizes.
        if (jobs.Any(j => j.Id != job.Id && !File.Exists(j.TargetPath) &&
            string.Equals(Path.GetDirectoryName(j.TargetPath), directory, StringComparison.OrdinalIgnoreCase) &&
            (j.DownloadedBytes > 0 ? j.DownloadedBytes : j.TotalBytes ?? j.ExpectedTotalBytes ?? 0) == size))
            return null;
        return candidates[0];
    }

    private static bool Matches(string path, long originalSize)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> marker = stackalloc byte[8];
            if (file.Read(marker) != marker.Length || !marker[..5].SequenceEqual("VDENC"u8)) return false;
            // Legacy format: five-byte marker; v2: eight-byte marker. Both encrypt the first 1024 bytes with PKCS7.
            var markerSize = marker.SequenceEqual("VDENC2\r\n"u8) ? 8 : 5;
            var headerSize = Math.Min(originalSize, VideoEncryptionService.OriginalHeaderSize);
            var encryptedHeader = (headerSize / 16 + 1) * 16;
            return file.Length == originalSize - headerSize + encryptedHeader + markerSize;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
