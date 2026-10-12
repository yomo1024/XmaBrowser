using System;
using System.Collections.Generic;

namespace XmaBrowser.Site;

/// <summary>
/// 记住「这条 Mod 的详情必须带着登录态才看得到」。
///
/// 由来（实测）：NSFW 是**客户端身份**门控 —— 匿名直连连公开内容都会被拦，
/// 而 NSFW 的详情页走直连只会拿到 403。问题是这个 403 是「需要登录」而不是
/// 「人机校验」，所以 DirectBlockMemory 的冷却对它不生效 ——
/// 结果每打开一条 NSFW Mod 都要先白撞一次直连（实测 936 ms），然后再走浏览器通道。
///
/// 这里按 modId 记下来（带 TTL，免得登录态变化后一直误判），下次直接走通道。
/// 纯逻辑、无网络依赖，可离线自测。
/// </summary>
public sealed class NeedsLoginMemory
{
    public const int DefaultCapacity = 300;
    public const int DefaultTtlSeconds = 6 * 3600;

    private readonly Dictionary<string, DateTime> _map = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly int _ttlSeconds;

    public NeedsLoginMemory(int capacity = DefaultCapacity, int ttlSeconds = DefaultTtlSeconds)
    {
        _capacity = Math.Max(1, capacity);
        _ttlSeconds = Math.Max(0, ttlSeconds);
    }

    public void Mark(string modId, DateTime nowUtc)
    {
        var key = (modId ?? string.Empty).Trim();
        if (key.Length == 0) return;

        // 重新插入让它变成「最新」，淘汰时先丢最久没被用到的
        _map.Remove(key);
        _map[key] = nowUtc;

        if (_map.Count <= _capacity) return;

        var stale = new List<string>();
        foreach (var kv in _map)
            if ((nowUtc - kv.Value).TotalSeconds > _ttlSeconds)
                stale.Add(kv.Key);
        foreach (var k in stale) _map.Remove(k);

        while (_map.Count > _capacity)
        {
            string? oldest = null;
            var oldestAt = DateTime.MaxValue;
            foreach (var kv in _map)
            {
                if (kv.Value >= oldestAt) continue;
                oldestAt = kv.Value;
                oldest = kv.Key;
            }
            if (oldest is null) break;
            _map.Remove(oldest);
        }
    }

    public bool Needs(string modId, DateTime nowUtc)
    {
        var key = (modId ?? string.Empty).Trim();
        if (key.Length == 0) return false;
        if (!_map.TryGetValue(key, out var at)) return false;

        if (_ttlSeconds > 0 && (nowUtc - at).TotalSeconds > _ttlSeconds)
        {
            _map.Remove(key);
            return false;
        }
        return true;
    }

    public void Clear() => _map.Clear();

    public int Count => _map.Count;
}
