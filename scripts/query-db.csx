#!/usr/bin/env dotnet-script
#r "nuget: Microsoft.Data.Sqlite, 9.0.0"

using Microsoft.Data.Sqlite;

var dbPath = @"C:\Users\刘志东\AppData\Local\VideoDownloader\data\downloads.db";
var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;

using var conn = new SqliteConnection(connStr);
conn.Open();

// Check download_jobs table
using var cmd1 = conn.CreateCommand();
cmd1.CommandText = "SELECT id, display_name, video_kind, is_encrypted, target_path, original_filename FROM download_jobs WHERE status='Completed' ORDER BY completed_at DESC LIMIT 20;";
using var reader1 = cmd1.ExecuteReader();
Console.WriteLine("=== Completed Jobs ===");
while (reader1.Read())
{
    Console.WriteLine($"ID: {reader1.GetString(0)}");
    Console.WriteLine($"  Name: {reader1.GetString(1)}");
    Console.WriteLine($"  Kind: {reader1.GetString(2)}");
    Console.WriteLine($"  Encrypted: {reader1.GetBoolean(3)}");
    Console.WriteLine($"  Path: {reader1.GetString(4)}");
    Console.WriteLine($"  OrigName: {(reader1.IsDBNull(5) ? "null" : reader1.GetString(5))}");
    Console.WriteLine();
}

// Check kinds table
using var cmd2 = conn.CreateCommand();
cmd2.CommandText = "SELECT value, label, is_private FROM library_kinds;";
using var reader2 = cmd2.ExecuteReader();
Console.WriteLine("=== Library Kinds ===");
while (reader2.Read())
{
    Console.WriteLine($"Value: {reader2.GetString(0)}, Label: {reader2.GetString(1)}, Private: {reader2.GetBoolean(2)}");
}
