using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Persistence;

/// <summary>In-memory catalog for unit tests (seeded like a fresh DB).</summary>
public sealed class MemoryLibraryKindCatalog : ILibraryKindCatalog
{
    private readonly List<LibraryKindEntry> _items;

    public MemoryLibraryKindCatalog()
    {
        _items = LibraryVideoKinds.Seed
            .Select((s, i) => new LibraryKindEntry(s.Value, s.Label, i, IsSeed: true))
            .ToList();
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<LibraryKindEntry> GetAll() => _items.ToList();

    public string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), LibraryVideoKinds.NewKindSentinel, StringComparison.Ordinal))
            return LibraryVideoKinds.Unspecified;
        var v = value.Trim().ToLowerInvariant();
        return _items.Any(k => k.Value == v) ? v : LibraryVideoKinds.Unspecified;
    }

    public string LabelOf(string? value)
    {
        var v = Normalize(value);
        if (v == LibraryVideoKinds.Unspecified)
            return LibraryVideoKinds.UnspecifiedLabel;
        return _items.FirstOrDefault(k => k.Value == v)?.Label ?? LibraryVideoKinds.UnspecifiedLabel;
    }

    public bool IsKnown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (string.Equals(value.Trim(), LibraryVideoKinds.NewKindSentinel, StringComparison.Ordinal))
            return false;
        var v = value.Trim().ToLowerInvariant();
        return _items.Any(k => k.Value == v);
    }

    public Task<LibraryKindEntry> AddAsync(string label, bool isPrivate = false, string? password = null, CancellationToken ct = default)
    {
        var trimmed = (label ?? string.Empty).Trim();
        if (trimmed.Length is < 1 or > 32)
            throw new InvalidOperationException("分类名称长度须为 1–32 个字符。");
        if (isPrivate && string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("隐私分类必须设置密码。");
        if (_items.Any(k => string.Equals(k.Label, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("分类名称已存在。");

        var value = SqliteLibraryKindCatalog.MakeSlug(trimmed);
        if (_items.Any(k => k.Value == value))
            value += "-" + (_items.Count + 1);
        var entry = new LibraryKindEntry(value, trimmed, 1000 + _items.Count, IsSeed: false, IsPrivate: isPrivate);
        _items.Add(entry);
        return Task.FromResult(entry);
    }

    public Task<bool> VerifyPasswordAsync(string kindValue, string password, CancellationToken ct = default)
    {
        var normalized = Normalize(kindValue);
        if (normalized == LibraryVideoKinds.Unspecified)
            return Task.FromResult(false);
        var kind = _items.FirstOrDefault(k => k.Value == normalized);
        return Task.FromResult(kind?.IsPrivate == true);
    }

    public Task RemovePrivacyAsync(string kindValue, string password, CancellationToken ct = default)
    {
        var normalized = Normalize(kindValue);
        if (normalized == LibraryVideoKinds.Unspecified)
            throw new InvalidOperationException("分类不存在。");
        var idx = _items.FindIndex(k => k.Value == normalized);
        if (idx >= 0 && _items[idx].IsPrivate)
        {
            var old = _items[idx];
            _items[idx] = old with { IsPrivate = false };
        }
        return Task.CompletedTask;
    }
}
