using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Dapper;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public class SqliteLibraryKindCatalogTests
{
    [Fact]
    public async Task Initialize_SeedsBuiltinKinds_Once()
    {
        await using var db = await OpenTempCatalogAsync();
        var all = db.Catalog.GetAll();
        Assert.Equal(LibraryVideoKinds.Seed.Count, all.Count);
        Assert.Contains(all, k => k.Value == "movie" && k.Label == "电影" && k.IsSeed);
        Assert.Equal("电影", db.Catalog.LabelOf("movie"));
        Assert.Equal(LibraryVideoKinds.UnspecifiedLabel, db.Catalog.LabelOf(""));
    }

    [Fact]
    public async Task Initialize_DoesNotOverwriteLocalSeedLabel()
    {
        await using var db = await OpenTempCatalogAsync();
        await using (var conn = new SqliteConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE library_kinds SET label = @Label WHERE value = 'movie'",
                new { Label = "自制电影" });
        }

        var again = new SqliteLibraryKindCatalog(Options.Create(new AppOptions
        {
            Database = new DatabaseOptions { Path = db.DbPath }
        }));
        await again.InitializeAsync();

        Assert.Equal("自制电影", again.LabelOf("movie"));
        Assert.DoesNotContain(again.GetAll(), k => k.Value == "movie" && k.Label == "电影");
    }

    [Fact]
    public async Task AddAsync_PersistsCustomKind()
    {
        await using var db = await OpenTempCatalogAsync();
        var created = await db.Catalog.AddAsync("纪录片");
        Assert.False(created.IsSeed);
        Assert.Equal("纪录片", created.Label);
        Assert.True(db.Catalog.IsKnown(created.Value));
        Assert.Equal("纪录片", db.Catalog.LabelOf(created.Value));

        var reload = new SqliteLibraryKindCatalog(Options.Create(new AppOptions
        {
            Database = new DatabaseOptions { Path = db.DbPath }
        }));
        await reload.InitializeAsync();
        Assert.Contains(reload.GetAll(), k => k.Value == created.Value && k.Label == "纪录片");
    }

    [Fact]
    public async Task AddAsync_RejectsDuplicateLabel()
    {
        await using var db = await OpenTempCatalogAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Catalog.AddAsync("电影"));
    }

    private static async Task<TempCatalog> OpenTempCatalogAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "vd-kinds-" + Guid.NewGuid().ToString("N") + ".db");
        var catalog = new SqliteLibraryKindCatalog(Options.Create(new AppOptions
        {
            Database = new DatabaseOptions { Path = path }
        }));
        await catalog.InitializeAsync();
        return new TempCatalog(path, catalog);
    }

    private sealed class TempCatalog(string dbPath, SqliteLibraryKindCatalog catalog) : IAsyncDisposable
    {
        public string DbPath { get; } = dbPath;
        public SqliteLibraryKindCatalog Catalog { get; } = catalog;
        public string ConnectionString =>
            new SqliteConnectionStringBuilder { DataSource = DbPath }.ConnectionString;

        public ValueTask DisposeAsync()
        {
            try { File.Delete(DbPath); } catch { /* ignore */ }
            return ValueTask.CompletedTask;
        }
    }
}
