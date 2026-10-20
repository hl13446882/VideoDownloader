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

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, string> _lastWin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ledgerPath;
    private int _persistScheduled;
    private bool _persistDirty;

    public ProbeMethodStatsStore(ILogger<ProbeMethodStatsStore> logger)
        : this(logger, DefaultLedgerPath())
    {
    }

    public ProbeMethodStatsStore(ILogger logger, string ledgerPath)
    {
        _logger = logger;
        _ledgerPath = ledgerPath;
        LoadUnlocked();
    }

    public static string DefaultLedgerPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "probe-method-stats.json");

    public void Record(string detectorId, string method, bool success) =>
        RecordCore(detectorId, method, success, null);

    public void RecordTimed(string detectorId, string method, bool success, TimeSpan elapsed) =>
        RecordCore(detectorId, method, success, Math.Max(1, elapsed.TotalMilliseconds));

    private void RecordCore(string detectorId, string method, bool success, double? elapsedMs)
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
            // A small rolling window adapts after cookies, tools or site behavior change.
            c.RecentAttempts = c.RecentAttempts * 0.9 + 1;
            c.RecentSuccesses = c.RecentSuccesses * 0.9 + (success ? 1 : 0);
            if (elapsedMs is > 0)
            {
                if (success)
                    c.SuccessMs = c.SuccessMs is > 0 ? c.SuccessMs * 0.75 + elapsedMs.Value * 0.25 : elapsedMs.Value;
                else
                    c.FailureMs = c.FailureMs is > 0 ? c.FailureMs * 0.75 + elapsedMs.Value * 0.25 : elapsedMs.Value;
            }
            c.Attempts++;
            if (success)
            {
                c.Successes++;
                _lastWin[detectorId] = method;
            }
            _persistDirty = true;
        }
        // Disk flush is deferred: YouTube ResolveAsync may Record many times between yt-dlp
        // attempts; blocking WriteAllText on each failure was pure wait on the hot path.
        RequestPersist();
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
            return (double)c.Successes / c.Attempts;
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

    public IReadOnlyList<T> OrderBySuccessCount<T>(string detectorId, IReadOnlyList<T> items, Func<T, string> methodOf)
    {
        if (items.Count <= 1) return items;
        lock (_gate)
        {
            return items
                .Select((item, index) => (item, index, score: SuccessRate(detectorId, methodOf(item))))
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.index)
                .Select(x => x.item)
                .ToArray();
        }
    }

    public IReadOnlyList<string> OrderedMethods(string detectorId) =>
        OrderBySuccessCount(detectorId,
            string.Equals(detectorId, SiteIds.YouTube, StringComparison.OrdinalIgnoreCase)
                ? ProbeDetectors.MethodsOf(detectorId)
                : ProbeDetectors.CallableMethodsOf(detectorId), method => method);

    public bool PreferYtdlpFirst(string detectorId) =>
        ProbeDetectors.IsYtDlpMethod(OrderedMethods(detectorId).FirstOrDefault());

    public int MethodSharePercent(string detectorId, string method)
    {
        if (string.IsNullOrWhiteSpace(detectorId) || string.IsNullOrWhiteSpace(method))
            return 0;
        lock (_gate)
        {
            var total = 0;
            var mine = 0;
            foreach (var counter in _counters.Values)
            {
                if (!string.Equals(counter.DetectorId, detectorId, StringComparison.OrdinalIgnoreCase))
                    continue;
                total += counter.Successes;
                if (string.Equals(counter.Method, method, StringComparison.OrdinalIgnoreCase))
                    mine = counter.Successes;
            }

            if (total <= 0)
                return 0;
            return (int)Math.Round(100.0 * mine / total, MidpointRounding.AwayFromZero);
        }
    }

    public event EventHandler<ProbeMethodNotice>? MethodStarted;

    public void ReportMethod(string detectorId, string method)
    {
        if (string.IsNullOrWhiteSpace(detectorId) || string.IsNullOrWhiteSpace(method))
            return;
        var attempts = Attempts(detectorId, method);
        var percent = attempts == 0 ? 0 : (int)Math.Round(100.0 * Successes(detectorId, method) / attempts);
        MethodStarted?.Invoke(this, new ProbeMethodNotice(detectorId, method, percent));
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

        sb.AppendLine("## 方法含义（真正取址方法；按探测器适用）");
        sb.AppendLine();
        sb.AppendLine("| 方法 | YouTube | B站 | 抖音 | TikTok | 角色 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine("| `ytdlp.pot_mweb` | 主 | ✗ | ✗ | ✗ | mweb + PO Token |");
        sb.AppendLine("| `ytdlp.default` | 回退 | ✗ | ✗ | ✗ | yt-dlp 默认 client |");
        sb.AppendLine("| `ytdlp.web_embedded` | 回退 | ✗ | ✗ | ✗ | embeddable only |");
        sb.AppendLine("| `playurl_api` | ✗ | 主 | ✗ | ✗ | wbi playurl → DASH |");
        sb.AppendLine("| `network_playurl` | ✗ | 回退 | ✗ | ✗ | 浏览器捕获 playurl/upos |");
        sb.AppendLine("| `ytdlp` | ✗ | 回退 | ✗ | 主 | 站点 yt-dlp extractor |");
        sb.AppendLine("| `aweme_detail` | ✗ | ✗ | 主 | ✗ | 拦截 aweme/detail JSON |");
        sb.AppendLine("| `network_media` | 最后回退 | ✗ | 回退 | 回退 | CDN / play_addr 流 |");
        sb.AppendLine("| `video_element` | ✗ | ✗ | 回退 | 回退 | 当前 video 元素 |");
        sb.AppendLine("| `router_data` | ✗ | ✗ | 回退 | ✗ | `_ROUTER_DATA` 等 |");
        sb.AppendLine("| `web_data` | ✗ | ✗ | ✗ | 回退 | SIGI_STATE / UNIVERSAL_DATA |");
        sb.AppendLine("| `album` | ✗ | ✗ | 图集 | 图集 | 图集独立路径 |");
        sb.AppendLine();
        sb.AppendLine("> 不再把 `dom_observation` / `browser_play` / `ytdlp.url:embed|canonical` 列为平级核心方法。");
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
                if (c.RecentAttempts <= 0 && c.Attempts > 0)
                {
                    c.RecentAttempts = Math.Min(10, c.Attempts);
                    c.RecentSuccesses = c.RecentAttempts * c.Successes / c.Attempts;
                }
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

    private void RequestPersist()
    {
        if (Interlocked.CompareExchange(ref _persistScheduled, 1, 0) != 0)
            return;
        _ = FlushPersistAsync();
    }

    private async Task FlushPersistAsync()
    {
        try
        {
            // Coalesce rapid Record calls (YouTube multi-client / cookie retries).
            await Task.Delay(250).ConfigureAwait(false);
            while (true)
            {
                lock (_gate)
                {
                    if (!_persistDirty)
                        break;
                    _persistDirty = false;
                    PersistUnlocked();
                }

                // Another Record may have dirtied during WriteAllText — flush once more without
                // another 250ms delay so we do not leave durable state behind.
                lock (_gate)
                {
                    if (!_persistDirty)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deferred probe-method-stats persist failed");
        }
        finally
        {
            Interlocked.Exchange(ref _persistScheduled, 0);
            lock (_gate)
            {
                if (_persistDirty)
                    RequestPersist();
            }
        }
    }

    private static string Key(string detectorId, string method) => detectorId + "\u001f" + method;

    private static int DetectorSort(string detectorId) => detectorId switch
    {
        SiteIds.YouTube => 0,
        SiteIds.Bilibili => 1,
        SiteIds.Douyin => 2,
        SiteIds.TikTok => 3,
        SiteIds.Generic => 4,
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
        public double RecentAttempts { get; set; }
        public double RecentSuccesses { get; set; }
        public double SuccessMs { get; set; }
        public double FailureMs { get; set; }
    }

    private sealed class LedgerFile
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public string? Note { get; set; }
        public List<Counter> Counters { get; set; } = [];
        public Dictionary<string, string>? LastWin { get; set; }
    }
}
