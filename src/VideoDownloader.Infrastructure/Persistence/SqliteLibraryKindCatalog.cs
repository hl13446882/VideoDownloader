using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed library categories. Seed rows use INSERT OR IGNORE (labels never overwritten).
/// </summary>
public sealed class SqliteLibraryKindCatalog : ILibraryKindCatalog
{
    private static readonly Regex AsciiSlug = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly object _gate = new();
    private List<LibraryKindEntry> _cache = [];

    public SqliteLibraryKindCatalog(IOptions<AppOptions> options)
    {
        var dbPath = PathExpander.Expand(options.Value.Database.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS library_kinds (
                value TEXT PRIMARY KEY NOT NULL,
                label TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                is_seed INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_library_kinds_sort ON library_kinds(sort_order, label);
            """);

        var now = DateTimeOffset.UtcNow.ToString("O");
        for (var i = 0; i < LibraryVideoKinds.Seed.Count; i++)
        {
            var (value, label) = LibraryVideoKinds.Seed[i];
            // INSERT OR IGNORE: upgrades never overwrite a local label / custom row.
            await conn.ExecuteAsync("""
                INSERT OR IGNORE INTO library_kinds(value, label, sort_order, is_seed, created_at)
                VALUES (@Value, @Label, @SortOrder, 1, @CreatedAt);
                """, new
            {
                Value = value,
                Label = label,
                SortOrder = i,
                CreatedAt = now
            });
        }

        await ReloadCacheAsync(conn);
    }

    public IReadOnlyList<LibraryKindEntry> GetAll()
    {
        lock (_gate)
            return _cache.ToList();
    }

    public string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), LibraryVideoKinds.NewKindSentinel, StringComparison.Ordinal))
            return LibraryVideoKinds.Unspecified;

        var v = value.Trim().ToLowerInvariant();
        lock (_gate)
        {
            return _cache.Any(k => string.Equals(k.Value, v, StringComparison.Ordinal))
                ? v
                : LibraryVideoKinds.Unspecified;
        }
    }

    public string LabelOf(string? value)
    {
        var v = Normalize(value);
        if (v == LibraryVideoKinds.Unspecified)
            return LibraryVideoKinds.UnspecifiedLabel;

        lock (_gate)
        {
            var hit = _cache.FirstOrDefault(k => string.Equals(k.Value, v, StringComparison.Ordinal));
            return hit?.Label ?? LibraryVideoKinds.UnspecifiedLabel;
        }
    }

    public bool IsKnown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (string.Equals(value.Trim(), LibraryVideoKinds.NewKindSentinel, StringComparison.Ordinal))
            return false;

        var v = value.Trim().ToLowerInvariant();
        lock (_gate)
            return _cache.Any(k => string.Equals(k.Value, v, StringComparison.Ordinal));
    }

    public async Task<LibraryKindEntry> AddAsync(string label, CancellationToken ct = default)
    {
        var trimmed = (label ?? string.Empty).Trim();
        if (trimmed.Length is < 1 or > 32)
            throw new InvalidOperationException("分类名称长度须为 1–32 个字符。");
        if (string.Equals(trimmed, LibraryVideoKinds.UnspecifiedLabel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能使用保留名称「未分类」。");

        lock (_gate)
        {
            if (_cache.Any(k => string.Equals(k.Label, trimmed, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("分类名称已存在。");
        }

        var value = AllocateValue(trimmed);
        var sortOrder = 1000;
        lock (_gate)
        {
            if (_cache.Count > 0)
                sortOrder = Math.Max(1000, _cache.Max(k => k.SortOrder) + 1);
        }

        var entry = new LibraryKindEntry(value, trimmed, sortOrder, IsSeed: false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        try
        {
            await conn.ExecuteAsync("""
                INSERT INTO library_kinds(value, label, sort_order, is_seed, created_at)
                VALUES (@Value, @Label, @SortOrder, 0, @CreatedAt);
                """, new
            {
                entry.Value,
                entry.Label,
                entry.SortOrder,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("分类已存在。");
        }

        await ReloadCacheAsync(conn);
        return entry;
    }

    private string AllocateValue(string label)
    {
        var baseValue = MakeSlug(label);
        lock (_gate)
        {
            if (!_cache.Any(k => string.Equals(k.Value, baseValue, StringComparison.Ordinal)))
                return baseValue;

            for (var n = 2; n < 1000; n++)
            {
                var candidate = $"{baseValue}-{n}";
                if (!_cache.Any(k => string.Equals(k.Value, candidate, StringComparison.Ordinal)))
                    return candidate;
            }
        }

        return "k" + Guid.NewGuid().ToString("N")[..10];
    }

    internal static string MakeSlug(string label)
    {
        var lower = label.Trim().ToLowerInvariant();
        var ascii = AsciiSlug.Replace(lower, "-").Trim('-');
        if (ascii.Length >= 2 && ascii.Length <= 40 && ascii.Any(char.IsLetterOrDigit))
            return ascii;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(label.Trim()));
        return "k" + Convert.ToHexString(hash).ToLowerInvariant()[..10];
    }

    private async Task ReloadCacheAsync(SqliteConnection conn)
    {
        var rows = (await conn.QueryAsync<KindRow>("""
            SELECT value, label, sort_order, is_seed
            FROM library_kinds
            ORDER BY sort_order ASC, label COLLATE NOCASE ASC
            """)).ToList();

        var list = rows.Select(r => new LibraryKindEntry(
            r.value,
            r.label,
            r.sort_order,
            r.is_seed != 0)).ToList();

        lock (_gate)
            _cache = list;
    }

    private sealed class KindRow
    {
        public string value { get; set; } = string.Empty;
        public string label { get; set; } = string.Empty;
        public int sort_order { get; set; }
        public int is_seed { get; set; }
    }
}
