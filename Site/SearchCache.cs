using System;
using System.Collections.Generic;

namespace XmaBrowser.Site;

/// <summary>
/// 搜索结果的**短期缓存**：同一条 URL 在 TTL 内直接复用。
///
/// 为什么需要：搜索本来就受站点响应时间约束（走代理时 1–5 秒，NSFW 模式还要过浏览器通道）。
/// 但「翻回上一页 / 取消筛选又搜回去 / 手滑点两次」这些动作重复请求同一 URL 是纯浪费。
/// 这里只做几分钟的记忆，站点内容变化不会被明显掩盖。
///
/// 纯逻辑、无网络依赖，可离线自测。
/// </summary>
public sealed class SearchCache
{
    public const int DefaultTtlSeconds = 180;
    public const int DefaultCapacity = 16;

    private readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly int _ttlSeconds;
    private readonly int _capacity;

    public SearchCache(int ttlSeconds = DefaultTtlSeconds, int capacity = DefaultCapacity)
    {
        _ttlSeconds = Math.Max(0, ttlSeconds);
        _capacity = Math.Max(1, capacity);
    }

    private sealed class Entry
    {
        public SearchResult Result { get; set; } = new();
        public DateTime StoredUtc { get; set; }
        public DateTime LastHitUtc { get; set; }
    }

    /// <summary>缓存键：完整 URL（含 page/筛选/nsfw 参数），所以不同筛选不会串。</summary>
    public static string KeyFor(string url) => (url ?? string.Empty).Trim();

    public bool TryGet(string url, out SearchResult result)
    {
        result = new SearchResult();
        if (_ttlSeconds == 0) return false;

        var key = KeyFor(url);
        if (key.Length == 0) return false;
        if (!_map.TryGetValue(key, out var entry)) return false;

        if ((DateTime.UtcNow - entry.StoredUtc).TotalSeconds > _ttlSeconds)
        {
            _map.Remove(key);
            return false;
        }

        entry.LastHitUtc = DateTime.UtcNow;
        result = entry.Result;
        return true;
    }

    public void Put(string url, SearchResult result)
    {
        if (_ttlSeconds == 0) return;

        var key = KeyFor(url);
        if (key.Length == 0) return;

        _map[key] = new Entry { Result = result, StoredUtc = DateTime.UtcNow, LastHitUtc = DateTime.UtcNow };

        // 超容量：先把过期的清掉；还不够就丢最久没被用过的
        if (_map.Count <= _capacity) return;

        var now = DateTime.UtcNow;
        var stale = new List<string>();
        foreach (var kv in _map)
            if ((now - kv.Value.StoredUtc).TotalSeconds > _ttlSeconds)
                stale.Add(kv.Key);
        foreach (var k in stale) _map.Remove(k);

        while (_map.Count > _capacity)
        {
            string? oldest = null;
            var oldestAt = DateTime.MaxValue;
            foreach (var kv in _map)
            {
                if (kv.Value.LastHitUtc >= oldestAt) continue;
                oldestAt = kv.Value.LastHitUtc;
                oldest = kv.Key;
            }
            if (oldest is null) break;
            _map.Remove(oldest);
        }
    }

    public void Clear() => _map.Clear();

    public int Count => _map.Count;
}
