using System;
using System.Collections.Generic;

namespace XmaBrowser.Site;

/// <summary>
/// 详情的**短期缓存**（和 SearchCache 一个思路，只是存的东西不同）。
///
/// 为什么需要：NSFW 的详情拿不到直连，每次都要走浏览器通道（实测 2.8–3.6 秒，
/// 还得算上被别人占着通道时的排队）。而用户看 Mod 的习惯就是来回点——
/// 「看一下这条 → 回列表 → 再看回来」每次都重来一遍，主观感受就是「读详情很慢」。
///
/// 这里只记几分钟，站点上的改动不会被明显掩盖。
/// 纯逻辑、无网络依赖，可离线自测。
/// </summary>
public sealed class DetailCache
{
    public const int DefaultTtlSeconds = 300;
    public const int DefaultCapacity = 16;

    private readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly int _ttlSeconds;
    private readonly int _capacity;

    public DetailCache(int ttlSeconds = DefaultTtlSeconds, int capacity = DefaultCapacity)
    {
        _ttlSeconds = Math.Max(0, ttlSeconds);
        _capacity = Math.Max(1, capacity);
    }

    private sealed class Entry
    {
        public ModDetail Detail { get; set; } = new();
        public DateTime StoredUtc { get; set; }
        public DateTime LastHitUtc { get; set; }
    }

    /// <summary>缓存键：modId（详情就是「一条 mod 一份」）。</summary>
    public static string KeyFor(string modId) => (modId ?? string.Empty).Trim();

    public bool TryGet(string modId, out ModDetail detail)
    {
        detail = new ModDetail();
        if (_ttlSeconds == 0) return false;

        var key = KeyFor(modId);
        if (key.Length == 0) return false;
        if (!_map.TryGetValue(key, out var entry)) return false;

        if ((DateTime.UtcNow - entry.StoredUtc).TotalSeconds > _ttlSeconds)
        {
            _map.Remove(key);
            return false;
        }

        entry.LastHitUtc = DateTime.UtcNow;
        detail = entry.Detail;
        return true;
    }

    public void Put(string modId, ModDetail detail)
    {
        if (_ttlSeconds == 0) return;

        var key = KeyFor(modId);
        if (key.Length == 0 || detail is null) return;

        _map[key] = new Entry { Detail = detail, StoredUtc = DateTime.UtcNow, LastHitUtc = DateTime.UtcNow };
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

    /// <summary>丢掉一条（比如发现它是「登录后可见」的空壳页，不该被记住）。</summary>
    public void Remove(string modId) => _map.Remove(KeyFor(modId));

    public void Clear() => _map.Clear();

    public int Count => _map.Count;
}
