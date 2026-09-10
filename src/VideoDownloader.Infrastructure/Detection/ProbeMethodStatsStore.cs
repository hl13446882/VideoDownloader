using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>
/// Persistent probe-method success ledger. Rewrites DOCS/probe-method-stats.md each committed round
/// and keeps a machine-readable JSON ledger for runtime reordering.
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

    public void Record(string siteId, string method, bool success)
    {
        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(method))
            return;
        var key = Key(siteId, method);
        lock (_gate)
        {
            if (!_counters.TryGetValue(key, out var c))
            {
                c = new Counter(siteId, method);
                _counters[key] = c;
            }
            c.Attempts++;
            if (success)
            {
                c.Successes++;
                _lastWin[siteId] = method;
            }
            PersistUnlocked();
        }
        _logger.LogInformation(
            "ProbeMethodStats site={Site} method={Method} success={Success} rate={Rate:P0} ({Ok}/{All})",
            siteId, method, success, SuccessRate(siteId, method), Successes(siteId, method), Attempts(siteId, method));
    }

    public double SuccessRate(string siteId, string method)
    {
        lock (_gate)
        {
            if (!_counters.TryGetValue(Key(siteId, method), out var c) || c.Attempts == 0)
                return 0.5; // uninformative prior — do not invent preference
            // Laplace smoothing so early samples do not swing hard.
            return (c.Successes + 1.0) / (c.Attempts + 2.0);
        }
    }

    public int Attempts(string siteId, string method)
    {
        lock (_gate)
            return _counters.TryGetValue(Key(siteId, method), out var c) ? c.Attempts : 0;
    }

    public int Successes(string siteId, string method)
    {
        lock (_gate)
            return _counters.TryGetValue(Key(siteId, method), out var c) ? c.Successes : 0;
    }

    public IReadOnlyList<T> OrderBySuccessRate<T>(string siteId, IReadOnlyList<T> items, Func<T, string> methodOf)
    {
        if (items.Count <= 1) return items;
        return items
            .Select((item, index) => (item, index, rate: SuccessRate(siteId, methodOf(item))))
            .OrderByDescending(x => x.rate)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToArray();
    }

    public bool PreferYtdlpFirst(string siteId)
    {
        var ytdlp = SuccessRate(siteId, ProbeMethods.YtDlp);
        var network = Math.Max(
            SuccessRate(siteId, ProbeMethods.NetworkCdn),
            Math.Max(
                SuccessRate(siteId, ProbeMethods.BrowserPlay),
                SuccessRate(siteId, ProbeMethods.DomObservation)));
        // Need real evidence that yt-dlp wins; otherwise keep site defaults.
        if (Attempts(siteId, ProbeMethods.YtDlp) < 2)
            return siteId is SiteIds.YouTube;
        return ytdlp >= network;
    }

    public string? LastWinningMethod(string siteId) =>
        _lastWin.TryGetValue(siteId, out var m) ? m : null;

    public IReadOnlyList<ProbeMethodStatRow> Snapshot()
    {
        lock (_gate)
        {
            return _counters.Values
                .OrderBy(c => c.SiteId, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(c => c.Attempts == 0 ? 0 : (double)c.Successes / c.Attempts)
                .ThenBy(c => c.Method, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ProbeMethodStatRow(
                    c.SiteId,
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
            entries,
            cumulative = Snapshot()
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
        sb.AppendLine("# 探测器方法成功率（probe-method-stats）");
        sb.AppendLine();
        sb.AppendLine("每轮 live 验收 / 探测结束后更新。运行时按成功率从高到低排列探测方法。");
        sb.AppendLine();
        sb.AppendLine($"- 最近一轮：`{runId}`");
        sb.AppendLine($"- 更新时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- 机器账本：`%LOCALAPPDATA%\\\\VideoDownloader\\\\probe-method-stats.json`");
        sb.AppendLine();
        sb.AppendLine("## 累计成功率（用于排序）");
        sb.AppendLine();
        sb.AppendLine("| 站点 | 方法 | 成功 | 尝试 | 成功率 | 排序建议 |");
        sb.AppendLine("|---|---|---:|---:|---:|---|");

        foreach (var siteGroup in snap.GroupBy(r => r.SiteId, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = siteGroup
                .OrderByDescending(r => r.Rate)
                .ThenByDescending(r => r.Attempts)
                .ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var r = ordered[i];
                sb.AppendLine(
                    $"| {r.SiteId} | `{r.Method}` | {r.Successes} | {r.Attempts} | {r.Rate:P0} | #{i + 1} |");
            }
        }

        if (snap.Count == 0)
            sb.AppendLine("| — | — | 0 | 0 | — | 尚无数据 |");

        sb.AppendLine();
        sb.AppendLine("## 本轮明细");
        sb.AppendLine();
        sb.AppendLine("| 站点 | 地址 | 获胜方法 | 通过 | 取址ms | 无效探测 |");
        sb.AppendLine("|---|---|---|---|---:|---|");
        foreach (var e in entries)
        {
            var addr = e.Address is null ? "" : (e.Address.Length > 64 ? e.Address[..64] + "…" : e.Address);
            var invalid = e.InvalidProbes is { Count: > 0 } ? string.Join(",", e.InvalidProbes) : "";
            sb.AppendLine(
                $"| {e.SiteId} | {addr} | `{(e.WinningMethod ?? "—")}` | {(e.Pass ? "PASS" : "FAIL")} | {e.DiscoveryMs?.ToString() ?? "—"} | {invalid} |");
        }

        if (entries.Count == 0)
            sb.AppendLine("| — | — | — | — | — | 本轮无明细 |");

        sb.AppendLine();
        sb.AppendLine("## 方法说明");
        sb.AppendLine();
        sb.AppendLine("- `ytdlp`：外部 yt-dlp 解析");
        sb.AppendLine("- `network_cdn`：网络拦截到的 CDN/progressive");
        sb.AppendLine("- `browser_play`：浏览器已播放（CDP Media）");
        sb.AppendLine("- `dom_observation`：页面 DOM / playAddr 观察");
        sb.AppendLine("- `album_images`：图集图片收集");
        sb.AppendLine("- `ytdlp.client:*`：YouTube player_client 变体");
        sb.AppendLine("- `ytdlp.url:*`：TikTok 等解析 URL 变体（embed / canonical）");
        sb.AppendLine("- `schedule:ytdlp_first` / `schedule:network_first`：外层调度策略");
        sb.AppendLine();
        sb.AppendLine("历史轮次 JSON：`DOCS/probe-method-stats-rounds/<runId>.json`");

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
                _counters[Key(c.SiteId, c.Method)] = c;
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
                Counters = _counters.Values.OrderBy(c => c.SiteId).ThenBy(c => c.Method).ToList(),
                LastWin = _lastWin.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(_ledgerPath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist probe-method-stats ledger");
        }
    }

    private static string Key(string siteId, string method) => siteId + "\u001f" + method;

    private static string FindDefaultMarkdownPath()
    {
        // Prefer repo DOCS when running from source/publish under the workspace.
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
        public Counter(string siteId, string method)
        {
            SiteId = siteId;
            Method = method;
        }
        public string SiteId { get; set; } = "";
        public string Method { get; set; } = "";
        public int Attempts { get; set; }
        public int Successes { get; set; }
    }

    private sealed class LedgerFile
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public List<Counter> Counters { get; set; } = [];
        public Dictionary<string, string>? LastWin { get; set; }
    }
}
