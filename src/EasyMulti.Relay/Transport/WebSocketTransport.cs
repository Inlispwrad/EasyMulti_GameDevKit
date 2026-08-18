#nullable enable

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using EasyMultiNet.Protocol;
using EasyMultiNet.Relay;

namespace EasyMultiNet.Relay.Transport;

/// <summary>
/// WebSocket transport built on BCL <see cref="HttpListener"/> + <see cref="AcceptWebSocketAsync"/>.
/// Zero third-party dependencies. TLS is intentionally out of scope — put a reverse proxy
/// (nginx/caddy) in front for <c>wss://</c>.
/// <para>
/// 凭证在**升级握手里**验（<c>Sec-WebSocket-Protocol</c>，见 <see cref="RelayHandshake"/>）：
/// 验不过就回 401、根本不升级，不会有任何对象被创建。反向代理必须透传这个头
/// —— caddy 的 reverse_proxy 默认透传，nginx 配 WebSocket 升级时一并带上。
/// </para>
/// </summary>
public sealed class WebSocketTransport : IRelayTransport
{
    private readonly int _port;
    private HttpListener? _listener;
    private Action<RelayEvent>? _enqueue;
    private Func<RegisterRequest, string, string?>? _authenticate;
    private readonly object _gate = new();
    private bool _stopped;

    public WebSocketTransport(int port) => _port = port;

    public void Start(Action<RelayEvent> enqueue, Func<RegisterRequest, string, string?> authenticate)
    {
        _enqueue = enqueue;
        _authenticate = authenticate;
        _listener = StartListener(_port);
        _ = AcceptLoop(_listener, enqueue, authenticate);
    }

    public void Tick()
    {
        // WebSocket needs no periodic bookkeeping — TCP handles reliability, and
        // connection teardown is driven by the socket's own close events.
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }

        // Close() shuts down immediately (discarding queued requests); Stop() can block
        // on the managed Unix listener waiting for pending GetContextAsync calls.
        try { _listener?.Close(); } catch { /* already closed */ }
        try { _listener?.Abort(); } catch { /* already aborted */ }
    }

    public void Dispose() => Stop();

    private static HttpListener StartListener(int port)
    {
        // '+' binds all interfaces; on Windows it needs an admin urlacl. Fall back to the
        // loopback prefixes, which a non-admin user may always bind (the same strategy as
        // PokerRush's DevRelay). Both spellings are registered because HttpListener matches
        // on the request's Host header: a client dialling 127.0.0.1 does *not* match a
        // "localhost" prefix, which used to make loopback clients fail on Windows.
        string[][] attempts =
        {
            new[] { $"http://+:{port}/" },
            new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" },
        };

        foreach (string[] prefixes in attempts)
        {
            var listener = new HttpListener();
            foreach (string prefix in prefixes)
            {
                listener.Prefixes.Add(prefix);
            }

            try
            {
                listener.Start();
                string listening = string.Join(" ", prefixes.Select(p => p.Replace("http://", "ws://")));
                Console.WriteLine($"[EasyMulti] WebSocket listening on {listening}");
                if (prefixes.Length > 1)
                {
                    Console.WriteLine(
                        "[EasyMulti] 只绑到了本机回环。要收本机以外的连接，用管理员跑一次："
                        + $"netsh http add urlacl url=http://+:{port}/ user=%USERNAME%");
                }

                return listener;
            }
            catch (HttpListenerException e)
            {
                Console.Error.WriteLine(
                    $"[EasyMulti] 绑定 {string.Join(" ", prefixes)} 失败（{e.Message}），换下一组");
                listener.Close();
            }
        }

        throw new InvalidOperationException($"端口 {port} 一个前缀都绑不上");
    }

    private static async Task AcceptLoop(
        HttpListener listener, Action<RelayEvent> enqueue, Func<RegisterRequest, string, string?> authenticate)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // listener stopped
            }

            _ = HandleContext(context, enqueue, authenticate);
        }
    }

    private static async Task HandleContext(
        HttpListenerContext context, Action<RelayEvent> enqueue, Func<RegisterRequest, string, string?> authenticate)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            // Health probes arrive as a plain GET. Load balancers and PaaS platforms mark a
            // service unhealthy on anything but 2xx, so answer /health with 200 — otherwise
            // "deploy it once and forget it" turns into a restart loop. Everything else
            // that is not an upgrade gets 426.
            bool health = string.Equals(context.Request.Url?.AbsolutePath, "/health", StringComparison.Ordinal);
            context.Response.StatusCode = health
                ? (int)HttpStatusCode.OK
                : (int)HttpStatusCode.UpgradeRequired;

            if (health)
            {
                byte[] body = Encoding.UTF8.GetBytes("ok");
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.ContentLength64 = body.Length;
                try { context.Response.OutputStream.Write(body, 0, body.Length); }
                catch (Exception) { /* probe hung up */ }
            }

            context.Response.Close();
            return;
        }

        // 凭证在升级之前验完。不合格的请求拿不到 WebSocket，也就没有任何对象被创建 ——
        // 这是「没证明身份就不占资源」的落点。浏览器读不到 401 的状态码（WebSocket 规范
        // 有意不给 JS），但走到这里的只有 token/gameId 配错的开发者，中继日志里有原因；
        // 玩家会碰到的 name_taken 不在这拦，那是核心线程上的事。
        string address = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
        if (!RelayHandshake.TryDecode(SubProtocols(context.Request), out RegisterRequest credentials))
        {
            Console.Error.WriteLine($"[EasyMulti] 拒绝升级 {address}：没有可解析的凭证子协议");
            Refuse(context, HttpStatusCode.Unauthorized);
            return;
        }

        if (authenticate(credentials, address) is string refusal)
        {
            Console.Error.WriteLine($"[EasyMulti] 拒绝升级 {address}：{refusal}");
            Refuse(context, HttpStatusCode.Unauthorized);
            return;
        }

        HttpListenerWebSocketContext socketContext;
        try
        {
            socketContext = await context.AcceptWebSocketAsync(RelayHandshake.Protocol).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[EasyMulti] WebSocket 握手失败：" + e.Message);
            context.Response.Abort();
            return;
        }

        var connection = new WebSocketConnection(socketContext.WebSocket, address, credentials, enqueue);
        await connection.RunAsync().ConfigureAwait(false);
    }

    /// <summary>客户端提出的子协议名列表（<c>Sec-WebSocket-Protocol</c> 是逗号分隔的）。</summary>
    private static IEnumerable<string> SubProtocols(HttpListenerRequest request)
    {
        string? raw = request.Headers["Sec-WebSocket-Protocol"];
        return string.IsNullOrEmpty(raw)
            ? Array.Empty<string>()
            : raw!.Split(',');
    }

    private static void Refuse(HttpListenerContext context, HttpStatusCode status)
    {
        context.Response.StatusCode = (int)status;
        try { context.Response.Close(); } catch (Exception) { /* peer hung up */ }
    }
}

/// <summary>
/// A single WebSocket client connection. Receive and send run on background pumps;
/// everything is handed to the relay core through the signaling enqueue, keeping the
/// core single-threaded and lock-free.
/// </summary>
public sealed class WebSocketConnection : IRelayConnection
{
    private const int ReceiveChunkBytes = 8 * 1024;

    private static int _nextId;

    private readonly WebSocket _socket;
    private readonly Action<RelayEvent> _enqueue;
    private readonly Channel<(string? Text, byte[]? Binary)> _outbox =
        Channel.CreateUnbounded<(string? Text, byte[]? Binary)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();
    private string? _closeReason;

    public WebSocketConnection(
        WebSocket socket, string address, RegisterRequest credentials, Action<RelayEvent> enqueue)
    {
        _socket = socket;
        _enqueue = enqueue;
        Address = address;
        Credentials = credentials;
        Id = $"ws-{Interlocked.Increment(ref _nextId)}";
    }

    public string Id { get; }
    public RegisterRequest Credentials { get; }
    public string Address { get; }
    public string TransportName => "WebSocket";

    public double UptimeSeconds => _uptime.Elapsed.TotalSeconds;

    public void Send(string json, DeliveryMode mode) => _outbox.Writer.TryWrite((json, null));

    public void SendBinary(byte[] frame, DeliveryMode mode) => _outbox.Writer.TryWrite((null, frame));

    public void Close(string reason)
    {
        _closeReason ??= reason;
        _outbox.Writer.TryComplete();
    }

    public async Task RunAsync()
    {
        _enqueue(new RelayEvent(RelayEventKind.Connected, this, null, null, "", DeliveryMode.Reliable));
        Task send = PumpOutboxAsync();
        Task receive = PumpInboxAsync();
        await Task.WhenAny(send, receive).ConfigureAwait(false);
        _outbox.Writer.TryComplete();
        _socket.Dispose();
        _enqueue(new RelayEvent(
            RelayEventKind.Disconnected, this, null, null,
            _closeReason ?? "连接已关闭", DeliveryMode.Reliable));
    }

    private async Task PumpOutboxAsync()
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out (string? Text, byte[]? Binary) message))
                {
                    byte[] bytes = message.Binary ?? Encoding.UTF8.GetBytes(message.Text!);
                    WebSocketMessageType kind = message.Binary != null
                        ? WebSocketMessageType.Binary
                        : WebSocketMessageType.Text;
                    await _socket.SendAsync(new ArraySegment<byte>(bytes), kind, endOfMessage: true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception e)
        {
            _closeReason ??= "发送失败：" + e.Message;
        }
    }

    private async Task PumpInboxAsync()
    {
        byte[] chunk = new byte[ReceiveChunkBytes];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(chunk), CancellationToken.None)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _closeReason ??= $"peer 发来 Close 帧（{result.CloseStatus} {result.CloseStatusDescription}）";
                        return;
                    }

                    message.Write(chunk, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    _enqueue(new RelayEvent(
                        RelayEventKind.Message, this, null, message.ToArray(), "", DeliveryMode.Reliable));
                }
                else
                {
                    string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    _enqueue(new RelayEvent(
                        RelayEventKind.Message, this, json, null, "", DeliveryMode.Reliable));
                }

                message.SetLength(0);
            }
        }
        catch (Exception e)
        {
            _closeReason ??= "接收失败：" + e.Message;
        }
    }
}
