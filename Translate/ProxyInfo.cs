using System;
using System.Net.Http;

namespace XmaBrowser.Translate;

/// <summary>
/// 查「插件实际会用哪个代理」。
///
/// 用 `HttpClient.DefaultProxy.GetProxy(目标)` 而不是自己去读注册表：
/// 它已经把系统代理开关、绕过清单（ProxyOverride）、环境变量都算进去了 —— 也就是**插件真实的行为**。
/// （实测：本机系统代理 127.0.0.1:7877、无 PAC、绕过清单里没有 google/xivmodarchive，
///   谷歌请求确实走了它 → 翻译后端选「谷歌」可直接用。）
///
/// 注意：只适用于 .NET Core / .NET 5+（插件跑在 .NET 10 上）。
/// Windows PowerShell 5.1 是 .NET Framework，`HttpClient.DefaultProxy` 为 null，测不出来 —— 别用它验证。
/// </summary>
public static class ProxyInfo
{
    private static readonly Uri GoogleProbe = new("https://translate.googleapis.com/translate_a/single");

    private static string _cached = string.Empty;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>给谷歌用的代理；null = 直连（没检测到代理）。</summary>
    public static Uri? ResolvedForGoogle()
    {
        try
        {
            var proxy = HttpClient.DefaultProxy.GetProxy(GoogleProbe);
            if (proxy is null) return null;

            // GetProxy 在「不需要代理」时会原样返回目标地址
            if (string.Equals(proxy.Host, GoogleProbe.Host, StringComparison.OrdinalIgnoreCase) &&
                proxy.Port == GoogleProbe.Port)
                return null;

            return proxy;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>给人看的说明，带缓存（设置面板每帧都画，别每帧去查一次系统设置）。</summary>
    public static string Describe()
    {
        if (_cached.Length > 0 && (DateTime.UtcNow - _cachedAt) < TimeSpan.FromSeconds(5))
            return _cached;

        var proxy = ResolvedForGoogle();
        _cached = proxy is null
            ? "直连（没检测到代理）"
            : $"{proxy.Scheme}://{proxy.Host}:{proxy.Port}";
        _cachedAt = DateTime.UtcNow;
        return _cached;
    }

    /// <summary>是否检测到代理（谷歌需要它）。</summary>
    public static bool HasProxy => ResolvedForGoogle() is not null;
}
