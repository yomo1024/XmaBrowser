using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XmaBrowser.Browser;

/// <summary>
/// 极简 Chrome DevTools Protocol 客户端（ClientWebSocket + System.Text.Json，无第三方依赖）。
/// 只用到三件事：取 Cookie、取页面 HTML、让浏览器真实下载一个文件。
/// </summary>
internal sealed class CdpClient : IDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _nextId;

    private CdpClient(ClientWebSocket ws) => _ws = ws;

    public static async Task<CdpClient> ConnectAsync(string wsUrl, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        // 刻意**不设** Origin 头：Chrome ≥111 的 CDP 会对带 Origin 的 WebSocket 握手直接回 403
        // （本机实测：带 Origin: http://127.0.0.1 → "403 when 101 was expected"；不带 → 正常）。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        await ws.ConnectAsync(new Uri(wsUrl), cts.Token).ConfigureAwait(false);
        return new CdpClient(ws);
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct,
                                             int timeoutMs = 60_000)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            while (true)
            {
                var json = await ReceiveAsync(cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var rid) &&
                    rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == id)
                    return doc.RootElement.Clone();
                // 其余是事件（Page.* / Network.*）：不关心，继续等自己的应答
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>从 Runtime.evaluate 的应答里取字符串值；出错返回 null。</summary>
    public static string? Value(JsonElement response)
    {
        try
        {
            if (response.TryGetProperty("result", out var res) &&
                res.TryGetProperty("result", out var inner) &&
                inner.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch { /* 忽略 */ }
        return null;
    }

    private async Task<string> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new IOException("CDP 连接被关闭");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        try { _ws.Dispose(); } catch { /* 忽略 */ }
        _lock.Dispose();
    }
}
