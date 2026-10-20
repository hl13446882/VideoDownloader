using Dapper;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "VideoDownloader", "data", "downloads.db");

Console.WriteLine($"Database: {dbPath}");
Console.WriteLine($"Exists: {File.Exists(dbPath)}");

if (!File.Exists(dbPath))
    return;

var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
using var conn = new SqliteConnection(connStr);
conn.Open();

var stats = conn.QueryFirst(@"
    SELECT
        COUNT(*) as total,
        SUM(CASE WHEN thumb_ready=1 THEN 1 ELSE 0 END) as thumb_ready,
        SUM(CASE WHEN status=4 THEN 1 ELSE 0 END) as completed,
        SUM(CASE WHEN status=4 AND thumb_ready=0 THEN 1 ELSE 0 END) as needs_thumb
    FROM download_jobs");

Console.WriteLine($"\nStats:");
Console.WriteLine($"  Total jobs: {stats.total}");
Console.WriteLine($"  Completed: {stats.completed}");
Console.WriteLine($"  Thumb ready: {stats.thumb_ready}");
Console.WriteLine($"  Needs thumb: {stats.needs_thumb}");

var pending = conn.Query(@"
    SELECT id, display_name, thumb_ready, target_path
    FROM download_jobs
    WHERE status=4 AND target_path != ''
    LIMIT 10").ToList();

Console.WriteLine($"\nSample completed jobs:");
foreach (var job in pending)
{
    var id = job.id.ToString().Substring(0, 8);
    var name = job.display_name.ToString().Substring(0, Math.Min(30, job.display_name.ToString().Length));
    Console.WriteLine($"  {id}... thumb={job.thumb_ready} {name}");
}
