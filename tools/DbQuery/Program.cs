using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

var dbPath = @"C:\Users\刘志东\AppData\Local\VideoDownloader\data\downloads.db";
var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;

using var conn = new SqliteConnection(connStr);
conn.Open();

// Get all videos in private category that are not marked as encrypted
using var cmd1 = conn.CreateCommand();
cmd1.CommandText = "SELECT id, target_path, video_kind, downloaded_bytes, total_bytes, expected_total_bytes FROM download_jobs WHERE video_kind='ke25d2a5830' AND encrypted_filename IS NULL;";
using var reader1 = cmd1.ExecuteReader();

var updates = new List<(Guid id, string originalPath, string directory, string originalFilename, long expectedSize)>();

while (reader1.Read())
{
    var id = Guid.Parse(reader1.GetString(0));
    var targetPath = reader1.GetString(1);
    var directory = Path.GetDirectoryName(targetPath) ?? "";
    var originalFilename = Path.GetFileName(targetPath);
    var downloadedBytes = reader1.IsDBNull(3) ? 0 : reader1.GetInt64(3);
    var totalBytes = reader1.IsDBNull(4) ? 0 : reader1.GetInt64(4);
    var expectedTotalBytes = reader1.IsDBNull(5) ? 0 : reader1.GetInt64(5);

    // Use the largest non-zero size value
    var expectedSize = Math.Max(downloadedBytes, Math.Max(totalBytes, expectedTotalBytes));

    updates.Add((id, targetPath, directory, originalFilename, expectedSize));
}

Console.WriteLine($"Found {updates.Count} videos to repair");

foreach (var (id, originalPath, directory, originalFilename, expectedSize) in updates)
{
    // Find the actual file in the directory (should be a hash-based .mp4 file)
    var files = Directory.GetFiles(directory, "*.mp4");
    if (files.Length == 0)
    {
        Console.WriteLine($"No files found in {directory} for video {id}");
        continue;
    }

    // Find which encrypted file matches the expected size
    string? encryptedFile = null;
    foreach (var file in files)
    {
        try
        {
            var fileInfo = new FileInfo(file);
            // Check if file is encrypted
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var marker = new byte[5];
            fs.Read(marker, 0, 5);
            if (System.Text.Encoding.ASCII.GetString(marker) != "VDENC")
                continue;

            // Check if size matches (encrypted file is slightly larger due to encryption overhead)
            // The overhead is about 27 bytes (5 marker + 32 padding - 10 original header)
            // But we'll use a tolerance of 1KB to account for any variations
            var sizeDiff = Math.Abs(fileInfo.Length - expectedSize);
            if (sizeDiff < 1024 || (expectedSize > 0 && (double)sizeDiff / expectedSize < 0.001))
            {
                encryptedFile = file;
                break;
            }
        }
        catch { }
    }

    if (encryptedFile == null)
    {
        Console.WriteLine($"No matching encrypted file found for video {id} (expected size: {expectedSize})");
        // List available files for debugging
        foreach (var file in files)
        {
            var fi = new FileInfo(file);
            Console.WriteLine($"  Available: {fi.Name} ({fi.Length} bytes)");
        }
        continue;
    }

    var encryptedFilename = Path.GetFileName(encryptedFile);
    Console.WriteLine($"Updating video {id}:");
    Console.WriteLine($"  Original: {originalFilename}");
    Console.WriteLine($"  Encrypted: {encryptedFilename}");
    Console.WriteLine($"  Size: {expectedSize} bytes");

    // Update the database
    using var updateCmd = conn.CreateCommand();
    updateCmd.CommandText = @"
        UPDATE download_jobs
        SET target_path = @targetPath,
            encrypted_filename = @encryptedFilename,
            original_filename = @originalFilename
        WHERE id = @id
    ";
    updateCmd.Parameters.AddWithValue("@targetPath", encryptedFile);
    updateCmd.Parameters.AddWithValue("@encryptedFilename", encryptedFilename);
    updateCmd.Parameters.AddWithValue("@originalFilename", originalFilename);
    updateCmd.Parameters.AddWithValue("@id", id.ToString());
    updateCmd.ExecuteNonQuery();
}

Console.WriteLine("Repair complete!");
Console.WriteLine();

// Verify the updates
using var verifyCmd = conn.CreateCommand();
verifyCmd.CommandText = "SELECT id, target_path, encrypted_filename, original_filename FROM download_jobs WHERE video_kind='ke25d2a5830';";
using var verifyReader = verifyCmd.ExecuteReader();
Console.WriteLine("=== Verification ===");
while (verifyReader.Read())
{
    Console.WriteLine($"ID: {verifyReader.GetString(0)}");
    Console.WriteLine($"  TargetPath: {verifyReader.GetString(1)}");
    Console.WriteLine($"  EncFilename: {(verifyReader.IsDBNull(2) ? "null" : verifyReader.GetString(2))}");
    Console.WriteLine($"  OrigFilename: {(verifyReader.IsDBNull(3) ? "null" : verifyReader.GetString(3))}");
    Console.WriteLine();
}
