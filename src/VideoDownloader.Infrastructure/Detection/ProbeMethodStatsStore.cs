using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>
/// Per-detector probe-method success ledger. Counters are keyed by (detectorId, method) —
/// YouTube rates never mix with Douyin/Bilibili/TikTok.
/// </summary>
public sealed class ProbeMethodStatsStore : IProbeMethodStats
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<ProbeMethodStatsStore> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, string> _lastWin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ledgerPath;

    public ProbeMethodStatsStore(ILogger<ProbeMethodStatsStore> logger)
    {
        _logger = logger;
        _ledgerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "probe-method-stats.json");
        LoadUnlocked();
    }

    public void Record(string detectorId, string method, bool success)
    {
        if (string.IsNullOrWhiteSpace(detectorId) || string.IsNullOrWhiteSpace(method))
            return;
        var key = Key(detectorId, method);
        lock (_gate)
        {
            if (!_counters.TryGetValue(key, out var c))
            {
                c = new Counter(detectorId, method);
                _counters[key] = c;
            }
            c.Attempts++;
            if (success)
            {
                c.Successes++;
                _lastWin[detectorId] = method;
            }
            PersistUnlocked();
        }
        _logger.LogInformation(
            "ProbeMethodStats detector={Detector} method={Method} success={Success} rate={Rate:P0} ({Ok}/{All})",
            detectorId, method, success, SuccessRate(detectorId, method), Successes(detectorId, method), Attempts(detectorId, method));
    }

    public double SuccessRate(string detectorId, string method)
    {
        lock (_gate)
        {
            if (!_counters.TryGetValue(Key(detectorId, method), out var c) || c.Attempts == 0)
                return 0.5;
            return (c.Successes + 1.0) / (c.Attempts + 2.0);
        }
    }

    public int Attempts(string detectorId, string method)
    {
        lock (_gate)
            return _counters.TryGetValue(Key(detectorId, method), out var c) ? c.Attempts : 0;
    }

    public int Successes(string detectorId, string method)
    {
        lock (_gate)
            return _counters.TryGetValue(Key(detectorId, method), out var c) ? c.Successes : 0;
    }

    public IReadOnlyList<T> OrderBySuccessRate<T>(string detectorId, IReadOnlyList<T> items, Func<T, string> methodOf)
    {
        if (items.Count <= 1) return items;
        return items
            .Select((item, index) => (item, index, rate: SuccessRate(detectorId, methodOf(item))))
            .OrderByDescending(x => x.rate)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToArray();
    }

    public bool PreferYtdlpFirst(string detectorId)
    {
        // Douyin (and any detector without yt-dlp) must never borrow YouTube/Bili rates.
        if (!ProbeDetectors.HasYtDlp(detectorId))
            return false;

        var ytdlp = SuccessRate(detectorId, ProbeMethods.YtDlp);
        var network = Math.Max(
            SuccessRate(detectorId, ProbeMethods.NetworkCdn),
            Math.Max(
                SuccessRate(detectorId, ProbeMethods.BrowserPlay),
                SuccessRate(detectorId, ProbeMethods.DomObservation)));
        if (Attempts(detectorId, ProbeMethods.YtDlp) < 2)
            return detectorId is SiteIds.YouTube;
        return ytdlp >= network;
    }

    public string? LastWinningMethod(string detectorId) =>
        _lastWin.TryGetValue(detectorId, out var m) ? m : null;

    public IReadOnlyList<ProbeMethodStatRow> Snapshot()
    {
        lock (_gate)
        {
            return _counters.Values
                .OrderBy(c => DetectorSort(c.DetectorId))
                .ThenByDescending(c => c.Attempts == 0 ? 0 : (double)c.Successes / c.Attempts)
                .ThenBy(c => c.Method, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ProbeMethodStatRow(
                    c.DetectorId,
                    ProbeDetectors.DisplayName(c.DetectorId),
                    c.Method,
                    c.Attempts,
                    c.Successes,
                    c.Attempts == 0 ? 0 : (double)c.Successes / c.Attempts))
                .ToArray();
        }
    }

    public void CommitRound(
        string runId,
        IReadOnlyList<ProbeMethodRoundEntry> entries,
        string? markdownDocPath = null,
        string? historyDir = null)
    {
        markdownDocPath ??= FindDefaultMarkdownPath();
        historyDir ??= Path.Combine(
            Path.GetDirectoryName(markdownDocPath) ?? ".",
            "probe-method-stats-rounds");

        Directory.CreateDirectory(historyDir);
        var roundPath = Path.Combine(historyDir, runId + ".json");
        var roundPayload = new
        {
            runId,
            finishedAt = DateTimeOffset.Now,
            note = "Rates are per-detector; never cross-mix youtube/bilibili/douyin/tiktok.",
            entries,
            byDetector = Snapshot()
                .GroupBy(r => r.DetectorId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.Rate).ThenByDescending(x => x.Attempts).ToArray(),
                    StringComparer.OrdinalIgnoreCase)
        };
        File.WriteAllText(roundPath, JsonSerializer.Serialize(roundPayload, JsonOptions));

        RewriteMarkdown(markdownDocPath, runId, entries);
        _logger.LogInformation("ProbeMethodStats round committed runId={RunId} doc={Doc}", runId, markdownDocPath);
    }

    private void RewriteMarkdown(string path, string runId, IReadOnlyList<ProbeMethodRoundEntry> entries)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var snap = Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine("# 探测器方法成功率（按探测器独立统计）");
        sb.AppendLine();
        sb.AppendLine("**硬规则：成功率只在同一探测器内部比较与排序。**");
        sb.AppendLine("抖音 / B站 / YouTube / TikTok 的方法账本互不混用（例如抖音没有 `ytdlp`，也不会用 YouTube 的 `ytdlp` 成功率）。");
        sb.AppendLine();
        sb.AppendLine($"- 最近一轮：`{runId}`");
        sb.AppendLine($"- 更新时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- 机器账本：`%LOCALAPPDATA%\\\\VideoDownloader\\\\probe-method-stats.json`（键 = detectorId + method）");
        sb.AppendLine();

        var detectorIds = ProbeDetectors.All
            .Concat(snap.Select(r => r.DetectorId))
            .Concat(entries.Select(e => e.DetectorId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(DetectorSort)
            .ToArray();

        foreach (var detectorId in detectorIds)
        {
            var name = ProbeDetectors.DisplayName(detectorId);
            sb.AppendLine($"## {name}（`{detectorId}`）");
            sb.AppendLine();
            sb.AppendLine("本探测器内方法成功率（仅与本探测器其它方法比较）：");
            sb.AppendLine();
            sb.AppendLine("| 排序 | 方法 | 成功 | 尝试 | 成功率 |");
            sb.AppendLine("|---:|---|---:|---:|---:|");

            var known = ProbeDetectors.MethodsOf(detectorId);
            var rows = snap.Where(r => string.Equals(r.DetectorId, detectorId, StringComparison.OrdinalIgnoreCase)).ToList();
            // Include catalog methods with 0 attempts so the detector's method set is visible.
            foreach (var method in known)
            {
                if (rows.Any(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)))
                    continue;
                rows.Add(new ProbeMethodStatRow(detectorId, name, method, 0, 0, 0));
            }
            // Also keep any recorded methods not in the static catalog (future variants).
            var ordered = rows
                .OrderByDescending(r => r.Attempts > 0 ? r.Rate : -1)
                .ThenByDescending(r => r.Attempts)
                .ThenBy(r => r.Method, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (ordered.Length == 0)
            {
                sb.AppendLine("| — | — | 0 | 0 | — |");
            }
            else
            {
                var rank = 0;
                foreach (var r in ordered)
                {
                    rank++;
                    var rateText = r.Attempts == 0 ? "—" : r.Rate.ToString("P0");
                    sb.AppendLine($"| #{rank} | `{r.Method}` | {r.Successes} | {r.Attempts} | {rateText} |");
                }
            }

            sb.AppendLine();
            sb.AppendLine("本轮该探测器明细：");
            sb.AppendLine();
            sb.AppendLine("| 地址 | 获胜方法 | 通过 | 取址ms | 无效探测 |");
            sb.AppendLine("|---|---|---|---:|---|");
            var detectorEntries = entries
                .Where(e => string.Equals(e.DetectorId, detectorId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (detectorEntries.Length == 0)
            {
                sb.AppendLine("| — | — | — | — | 本轮无该探测器样本 |");
            }
            else
            {
                foreach (var e in detectorEntries)
                {
                    var addr = e.Address is null ? "" : (e.Address.Length > 64 ? e.Address[..64] + "…" : e.Address);
                    var invalid = e.InvalidProbes is { Count: > 0 } ? string.Join(",", e.InvalidProbes) : "";
                    sb.AppendLine(
                        $"| {addr} | `{(e.WinningMethod ?? "—")}` | {(e.Pass ? "PASS" : "FAIL")} | {e.DiscoveryMs?.ToString() ?? "—"} | {invalid} |");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("## 方法含义（按探测器适用）");
        sb.AppendLine();
        sb.AppendLine("| 方法 | YouTube | B站 | 抖音 | TikTok | 含义 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine("| `ytdlp` | ✓ | ✓ | ✗ | ✓ | 外部 yt-dlp |");
        sb.AppendLine("| `network_cdn` | ✓ | ✓ | ✓ | ✓ | 网络 CDN / progressive |");
        sb.AppendLine("| `browser_play` | ✓ | ✓ | ✓ | ✓ | 浏览器已播放 |");
        sb.AppendLine("| `dom_observation` | ✓ | ✓ | ✓ | ✓ | DOM / playAddr |");
        sb.AppendLine("| `album_images` | ✗ | ✗ | ✓ | ✓ | 图集图片 |");
        sb.AppendLine("| `ytdlp.client:*` | ✓ | ✗ | ✗ | ✗ | YouTube player_client |");
        sb.AppendLine("| `ytdlp.url:*` | ✗ | ✗ | ✗ | ✓ | TikTok embed / canonical URL |");
        sb.AppendLine();
        sb.AppendLine("历史轮次 JSON：`DOCS/probe-method-stats-rounds/<runId>.json`（内含 `byDetector` 分组）");

        File.WriteAllText(path, sb.ToString());
    }

    private void LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_ledgerPath))
                return;
            var json = File.ReadAllText(_ledgerPath);
            var loaded = JsonSerializer.Deserialize<LedgerFile>(json, JsonOptions);
            if (loaded?.Counters is null) return;
            _counters.Clear();
            foreach (var c in loaded.Counters)
            {
                // Migrate older "siteId" field if present in JSON via DetectorId alias.
                var detectorId = string.IsNullOrWhiteSpace(c.DetectorId) ? c.SiteId : c.DetectorId;
                if (string.IsNullOrWhiteSpace(detectorId) || string.IsNullOrWhiteSpace(c.Method))
                    continue;
                c.DetectorId = detectorId;
                _counters[Key(detectorId, c.Method)] = c;
            }
            if (loaded.LastWin is not null)
            {
                foreach (var kv in loaded.LastWin)
                    _lastWin[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load probe-method-stats ledger");
        }
    }

    private void PersistUnlocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_ledgerPath)!;
            Directory.CreateDirectory(dir);
            var payload = new LedgerFile
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Note = "Keyed by detectorId+method; never share rates across detectors.",
                Counters = _counters.Values.OrderBy(c => DetectorSort(c.DetectorId)).ThenBy(c => c.Method).ToList(),
                LastWin = _lastWin.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(_ledgerPath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist probe-method-stats ledger");
        }
    }

    private static string Key(string detectorId, string method) => detectorId + "\u001f" + method;

    private static int DetectorSort(string detectorId) => detectorId switch
    {
        SiteIds.YouTube => 0,
        SiteIds.Bilibili => 1,
        SiteIds.Douyin => 2,
        SiteIds.TikTok => 3,
        _ => 9
    };

    private static string FindDefaultMarkdownPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            var docs = Path.Combine(dir, "DOCS");
            if (Directory.Exists(docs))
                return Path.Combine(docs, "probe-method-stats.md");
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "probe-method-stats.md");
    }

    private sealed class Counter
    {
        public Counter() { }
        public Counter(string detectorId, string method)
        {
            DetectorId = detectorId;
            Method = method;
        }

        public string DetectorId { get; set; } = "";
        /// <summary>Legacy JSON field name from first revision.</summary>
        public string SiteId { get; set; } = "";
        public string Method { get; set; } = "";
        public int Attempts { get; set; }
        public int Successes { get; set; }
    }

    private sealed class LedgerFile
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public string? Note { get; set; }
        public List<Counter> Counters { get; set; } = [];
        public Dictionary<string, string>? LastWin { get; set; }
    }
}
