using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XmaBrowser.Translate;

/// <summary>翻译器需要的设置（由 Configuration 实现，避免这里依赖插件主类）。</summary>
public interface ITranslateSettings
{
    string TranslateBackend { get; }
    string TranslateBaseUrl { get; }
    string TranslateApiKey { get; }
    string TranslateModel { get; }
}

/// <summary>
/// 英文 → 中文翻译（用于 Mod 标题与描述）。三个后端，都实测过（**注意：要用 .NET 栈测，curl 的结论不可信**）：
///
///   · <c>youdao</c>（**默认**）：有道公共接口 <c>https://aidemo.youdao.com/trans</c>，免 Key。
///     实测（.NET 走通）：单次能吃 593 字符；但**限流很紧（约 1 次/2 秒，且会进入惩罚期）**，
///     所以长文分段时要老实等够间隔，段落多时会比较慢。
///   · <c>tencent</c>：腾讯交互翻译 <c>https://transmart.qq.com/api/imt</c>，免 Key，质量与速度都更好
///     （curl 实测：一次 6000 字符、连打 8 次不限流、保留换行）。
///     ⚠ 但它**只发到中间证书**（DigiCert Secure Site OV G2 ... 2022 CA1），本机 Windows/SChannel
///     报「远程证书无效」，而 curl（Mozilla CA 包）能过 —— 即**同一接口在两套信任库下结论相反**。
///     换机器可能就好使，所以留着：报 SSL 错误就切回有道或用 openai。
///   · <c>openai</c>：任意 OpenAI 兼容接口（DeepSeek / 本地 vLLM 等），质量最好最快，需要 Key。
///     （实测 api.deepseek.com 的 TLS 正常。）
///
/// 另外实测**不通**、别再用：translate.googleapis.com（超时）、api.mymemory.translated.net（超时）、
/// lingva.ml（空响应）、api-edge.cognitive.microsofttranslator.com（空响应）、
/// cn.bing.com/ttranslatev3（statusCode 205，token/key 流程走不通）、
/// fanyi.youdao.com 的旧 translate 接口（302 到网页）。
///
/// 所有结果都落盘缓存（按文本哈希），列表里显示中文标题时只读缓存、绝不逐行发请求。
/// </summary>
public sealed class Translator : IDisposable
{
    /// <summary>腾讯接口单次请求的最大字符数（curl 实测 6000 可用；取 4500 留余量）。</summary>
    private const int TencentChunk = 4500;

    /// <summary>有道公共接口的最大字符数（实测 593 没问题；限流很紧，别贪多）。</summary>
    private const int YoudaoChunk = 400;

    /// <summary>LLM 单次请求的最大字符数（描述太长就分段，省 token 也避免超时）。</summary>
    private const int LlmChunk = 3000;

    private const string YoudaoUrl = "https://aidemo.youdao.com/trans";
    private const string TencentUrl = "https://transmart.qq.com/api/imt";

    private readonly HttpClient _http;
    private readonly ITranslateSettings _cfg;
    private readonly Action<string>? _log;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _saveLock = new();

    public Translator(HttpClient http, ITranslateSettings cfg, string cacheDirectory, Action<string>? log = null)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            _cachePath = Path.Combine(cacheDirectory, "translations.json");
            LoadCache();
        }
        catch
        {
            _cachePath = string.Empty;
        }
    }

    /// <summary>后端显示名（UI 里用）。</summary>
    public string BackendName => IsLlm ? $"OpenAI 兼容（{_cfg.TranslateModel}）"
                              : IsYoudao ? "有道公共接口（免 Key，默认）"
                                         : "腾讯交互翻译（免 Key）";

    private string BackendId => (_cfg.TranslateBackend ?? string.Empty).Trim().ToLowerInvariant();

    private bool IsLlm => BackendId == "openai";

    private bool IsYoudao => BackendId == "youdao";

    /// <summary>配置是否够用（LLM 后端需要 Key）。</summary>
    public bool IsConfigured => !IsLlm || (!string.IsNullOrWhiteSpace(_cfg.TranslateApiKey) &&
                                            !string.IsNullOrWhiteSpace(_cfg.TranslateBaseUrl));

    /// <summary>查缓存的译文（列表/宫格只用这个，不发请求）。</summary>
    public string? Cached(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return _cache.TryGetValue(Key(text), out var v) && v.Length > 0 ? v : null;
    }

    /// <summary>标题显示用：有译文就返回译文，否则返回原文。</summary>
    public string DisplayTitle(string title) => Cached(title) ?? title;

    /// <summary>翻译一段文本（带缓存）。</summary>
    public async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var key = Key(text);
        if (_cache.TryGetValue(key, out var hit) && hit.Length > 0) return hit;

        if (!IsConfigured)
            throw new InvalidOperationException("翻译后端还没配置好（设置 → 翻译：需要填 API Key / BaseUrl）");

        var result = IsLlm
            ? await TranslateViaLlmAsync(text, ct).ConfigureAwait(false)
            : IsYoudao
                ? await TranslateViaYoudaoAsync(text, ct).ConfigureAwait(false)
                : await TranslateViaTencentAsync(text, ct).ConfigureAwait(false);

        result = result.Trim();
        if (result.Length == 0) throw new InvalidOperationException("翻译接口回了空结果");

        _cache[key] = result;
        SaveCache();
        return result;
    }

    /// <summary>设置页的「测试」用：翻一句固定的英文并回报结果或错误。</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        const string probe = "Paint the Town - a cute mimic chest replacement.";
        var outp = await TranslateAsync(probe, ct).ConfigureAwait(false);
        return outp;
    }

    // ---------------------------------------------------------------- 腾讯交互翻译（免 Key，默认）

    private async Task<string> TranslateViaTencentAsync(string text, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var chunk in Split(text, TencentChunk))
        {
            ct.ThrowIfCancellationRequested();

            var payload = new
            {
                header = new
                {
                    fn = "auto_translation",
                    client_key = "browser-chrome-154",
                    client_type = "web",
                    client_version = "1.0.0",
                },
                type = "plain",
                model_category = "normal",
                source = new { lang = "en", text_list = new[] { chunk } },
                target = new { lang = "zh" },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, TencentUrl)
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.TryAddWithoutValidation("Referer", "https://transmart.qq.com/zh-CN/index");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"腾讯翻译接口 HTTP {(int)resp.StatusCode}：{Shorten(body)}");

            var (code, translated) = ReadTencent(body);
            if (code != "succ")
                throw new InvalidOperationException($"腾讯翻译接口返回 {code}（{Shorten(body)}）");
            if (translated.Length == 0)
                throw new InvalidOperationException("腾讯翻译接口没给出译文：" + Shorten(body));

            parts.Add(translated);
        }

        return string.Join("\n", parts);
    }

    private static (string Code, string Text) ReadTencent(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var code = node?["header"]?["ret_code"]?.GetValue<string>() ?? string.Empty;
            var text = node?["auto_translation"] is JsonArray { Count: > 0 } arr
                ? arr[0]?.GetValue<string>() ?? string.Empty
                : string.Empty;
            return (code, text);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    // ---------------------------------------------------------------- 有道（免 Key，备用；限流紧）

    private async Task<string> TranslateViaYoudaoAsync(string text, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var chunk in Split(text, YoudaoChunk))
        {
            ct.ThrowIfCancellationRequested();

            string translated = string.Empty;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var url = $"{YoudaoUrl}?from=auto&to=zh-CHS&q={Uri.EscapeDataString(chunk)}";
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"有道接口 HTTP {(int)resp.StatusCode}：{Shorten(body)}");

                translated = ReadYoudaoTranslation(body);
                if (translated.Length > 0) break;

                // 这个公共接口限流很紧（实测约 1 次/2 秒），被限流就等一下重试一次
                if (attempt < 2 && (body.Contains("频率过快") || body.Contains("411")))
                {
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    continue;
                }

                throw new InvalidOperationException("有道接口没给出译文（限流很紧，建议换腾讯后端）：" + Shorten(body));
            }

            parts.Add(translated);

            // 分段之间老老实实等够间隔，否则整段翻译必然被限流
            if (parts.Count > 1) await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        return string.Join("\n", parts);
    }

    private static string ReadYoudaoTranslation(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node?["translation"] is JsonArray arr && arr.Count > 0)
                return string.Concat(arr.Select(x => x?.GetValue<string>() ?? string.Empty));
            // 有的返回形态是 {"translateResult":[[{"tgt":"..."}]]}
            if (node?["translateResult"] is JsonArray rows)
            {
                var sb = new StringBuilder();
                foreach (var row in rows)
                    if (row is JsonArray cells)
                        foreach (var cell in cells)
                            sb.Append(cell?["tgt"]?.GetValue<string>() ?? string.Empty);
                return sb.ToString();
            }
        }
        catch { /* 交给调用方报错 */ }
        return string.Empty;
    }

    // ---------------------------------------------------------------- OpenAI 兼容

    private async Task<string> TranslateViaLlmAsync(string text, CancellationToken ct)
    {
        var baseUrl = _cfg.TranslateBaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/chat/completions";

        var outs = new List<string>();
        foreach (var chunk in Split(text, LlmChunk))
        {
            ct.ThrowIfCancellationRequested();

            var payload = new
            {
                model = _cfg.TranslateModel,
                temperature = 0.0,
                stream = false,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "你是翻译引擎。把用户给的英文《FFXIV Mod 说明》翻成简体中文。" +
                                  "保留原有换行；FFXIV 专有名词（Penumbra、TexTools、Gear、Hair 等）可保留英文。" +
                                  "只输出译文，不要解释、不要加任何前后缀。",
                    },
                    new { role = "user", content = chunk },
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.TranslateApiKey.Trim());

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"翻译接口 HTTP {(int)resp.StatusCode}：{Shorten(body)}");

            var content = ReadChatContent(body);
            if (content.Length == 0)
                throw new InvalidOperationException("翻译接口没给出内容：" + Shorten(body));
            outs.Add(content);
        }

        return string.Join("\n", outs);
    }

    private static string ReadChatContent(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------------------------------------------------------------- 分段 / 缓存

    /// <summary>按「空行 → 换行 → 句号 → 硬切」的优先级把长文切成 limit 以内的块。</summary>
    internal static IEnumerable<string> Split(string text, int limit)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split('\n');
        var buf = new StringBuilder();

        IEnumerable<string> Flush()
        {
            if (buf.Length == 0) yield break;
            var s = buf.ToString().Trim();
            buf.Clear();
            if (s.Length > 0) yield return s;
        }

        foreach (var raw in paragraphs)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                foreach (var f in Flush()) yield return f;
                continue;
            }

            if (line.Length > limit)
            {
                // 单行就超长 → 先吐缓冲，再按句子切这一行
                foreach (var f in Flush()) yield return f;
                foreach (var piece in SplitLongLine(line, limit)) yield return piece;
                continue;
            }

            if (buf.Length > 0 && buf.Length + line.Length + 1 > limit)
                foreach (var f in Flush()) yield return f;

            if (buf.Length > 0) buf.Append('\n');
            buf.Append(line);
        }

        foreach (var f in Flush()) yield return f;
    }

    private static IEnumerable<string> SplitLongLine(string line, int limit)
    {
        var start = 0;
        while (start < line.Length)
        {
            var take = Math.Min(limit, line.Length - start);
            if (start + take < line.Length)
            {
                // 往回收一点，尽量在句末/空格处断
                var window = line.LastIndexOfAny(['.', '!', '?', ';', '。', '！', '？'], start + take - 1,
                                                Math.Max(1, take / 2));
                if (window > start)
                    take = window - start + 1;
                else
                {
                    var sp = line.LastIndexOf(' ', start + take - 1, Math.Max(1, take / 2));
                    if (sp > start) take = sp - start;
                }
            }

            var piece = line.Substring(start, take).Trim();
            if (piece.Length > 0) yield return piece;
            start += take;
        }
    }

    private static string Key(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private void LoadCache()
    {
        if (_cachePath.Length == 0 || !File.Exists(_cachePath)) return;
        try
        {
            var json = File.ReadAllText(_cachePath, Encoding.UTF8);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null) return;
            foreach (var kv in dict)
                if (kv.Value.Length > 0)
                    _cache[kv.Key] = kv.Value;
            _log?.Invoke($"翻译缓存已载入 {_cache.Count} 条");
        }
        catch (Exception e)
        {
            _log?.Invoke("翻译缓存读取失败（忽略，重新累积）：" + e.Message);
        }
    }

    private void SaveCache()
    {
        if (_cachePath.Length == 0) return;
        lock (_saveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache.ToDictionary(kv => kv.Key, kv => kv.Value),
                                                    new JsonSerializerOptions { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.WriteAllText(_cachePath, json, Encoding.UTF8);
            }
            catch (Exception e)
            {
                _log?.Invoke("翻译缓存写入失败（忽略）：" + e.Message);
            }
        }
    }

    private static string Shorten(string s) => s.Length <= 160 ? s : s[..160] + "…";

    public void Dispose() => _gate.Dispose();
}
