using System.Text.Json;

namespace VideoDownloader.Infrastructure.Browser;

internal static class FrameAddressDiscovery
{
    internal static async Task<IReadOnlyList<string>> CollectAsync(
        Func<string, string, Task<string>> call, Func<bool> isCurrent, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        async Task<string> Invoke(string method, object args) =>
            await call(method, JsonSerializer.Serialize(args)).WaitAsync(timeout.Token);
        var frames = ParseFrames(await Invoke("Page.getFrameTree", new { }));
        var found = new List<(string Id, string Loader, string Url)>();
        foreach (var frame in frames.Skip(1).Take(16))
        {
            if (!isCurrent()) return [];
            try
            {
                using var world = JsonDocument.Parse(await Invoke("Page.createIsolatedWorld", new { frameId = frame.Id, worldName = "vd-address-discovery" }));
                var contextId = world.RootElement.GetProperty("executionContextId").GetInt32();
                using var value = JsonDocument.Parse(await Invoke("Runtime.evaluate", new
                { expression = MediaAddressDiscoveryScript.Expression, contextId, returnByValue = true }));
                if (value.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var entry in list.EnumerateArray().Take(64))
                        if (entry.TryGetProperty("url", out var address) && Uri.TryCreate(address.GetString(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                            found.Add((frame.Id, frame.Loader, url.AbsoluteUri));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* A detached or inaccessible frame does not invalidate other frames. */ }
        }
        if (!isCurrent()) return [];
        var current = ParseFrames(await Invoke("Page.getFrameTree", new { }));
        return !isCurrent() ? [] : found.Where(f => current.Contains((f.Id, f.Loader)))
            .Select(f => f.Url).Distinct(StringComparer.Ordinal).Take(64).ToArray();
    }

    private static List<(string Id, string Loader)> ParseFrames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var frames = new List<(string, string)>();
        Visit(doc.RootElement.GetProperty("frameTree"), 0);
        return frames;
        void Visit(JsonElement tree, int depth)
        {
            if (depth > 8 || frames.Count >= 17) return;
            var frame = tree.GetProperty("frame");
            frames.Add((frame.GetProperty("id").GetString()!, frame.GetProperty("loaderId").GetString()!));
            if (tree.TryGetProperty("childFrames", out var children))
                foreach (var child in children.EnumerateArray()) Visit(child, depth + 1);
        }
    }
}
