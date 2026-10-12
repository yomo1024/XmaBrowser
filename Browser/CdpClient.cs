using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XmaBrowser.Browser;

/// <summary>
/// CDP 连接已断（浏览器进程没了 / 连接被关 / 中途被 Dispose）。
/// 调用方应**丢弃会话重建**，而不是把原始 WebSocketException 抛给用户
/// （主人报过：WebSocketException: The WebSocket is in an invalid state ('Aborted')）。
/// </summary>
internal sealed class CdpClosedException : Exception
{
    public CdpClosedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 极简 Chrome DevTools Protocol 客户端（ClientWebSocket + System.Text.Json，无第三方依赖）。
/// 只用到三件事：取 Cookie、取页面 HTML、让浏览器真实下载一个文件。
/// </summary>
internal sealed class CdpClient : IDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _nextId;
    private volatile bool _dead;

    private CdpClient(ClientWebSocket ws) => _ws = ws;

    /// <summary>连接是否还能用（发之前必须看它，否则就是在往已死的 socket 上写）。</summary>
    public bool IsOpen
    {
        get
        {
            if (_dead) return false;
            try { return _ws.State == WebSocketState.Open; }
            catch { return false; }
        }
    }

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
        if (!IsOpen)
            throw new CdpClosedException("CDP 连接不可用（连接已关闭或已被释放）");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsOpen)
                throw new CdpClosedException("CDP 连接不可用（连接已关闭或已被释放）");

            var id = Interlocked.Increment(ref _nextId);
            var payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
            var bytes = Encoding.UTF8.GetBytes(payload);

            try
            {
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (IsClosedFailure(e))
            {
                _dead = true;
                throw new CdpClosedException($"发送 {method} 时连接中断：{e.Message}", e);
            }

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

    private static bool IsClosedFailure(Exception e)
        => e is WebSocketException or ObjectDisposedException or InvalidOperationException;

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
            WebSocketReceiveResult r;
            try
            {
                r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (IsClosedFailure(e))
            {
                _dead = true;
                throw new CdpClosedException($"接收数据时连接中断：{e.Message}", e);
            }

            if (r.MessageType == WebSocketMessageType.Close)
            {
                _dead = true;
                throw new CdpClosedException("CDP 连接被浏览器关闭");
            }

            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        _dead = true;
        try { _ws.Dispose(); } catch { /* 忽略 */ }
        _lock.Dispose();
    }
}
