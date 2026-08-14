#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using EasyMulti.Protocol;
using EasyMulti.Relay;

namespace EasyMulti.Relay.Transport;

/// <summary>
/// WebSocket transport built on BCL <see cref="HttpListener"/> + <see cref="AcceptWebSocketAsync"/>.
/// Zero third-party dependencies. TLS is intentionally out of scope — put a reverse proxy
/// (nginx/caddy) in front for <c>wss://</c>.
/// </summary>
public sealed class WebSocketTransport : IRelayTransport
{
    private readonly int _port;
    private HttpListener? _listener;
    private ConcurrentQueue<RelayEvent>? _events;
    private readonly object _gate = new();
    private bool _stopped;

    public WebSocketTransport(int port) => _port = port;

    public void Start(ConcurrentQueue<RelayEvent> events)
    {
        _events = events;
        _listener = StartListener(_port);
        _ = AcceptLoop(_listener, events);
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
        // '+' binds all interfaces; on Windows it needs an admin urlacl. Fall back to
        // localhost for out-of-the-box single-machine runs (the same strategy as PokerRush's DevRelay).
        foreach (string prefix in new[] { $"http://+:{port}/", $"http://localhost:{port}/" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                Console.WriteLine($"[EasyMulti] WebSocket listening on {prefix.Replace("http://", "ws://")}");
                if (prefix.Contains("localhost"))
                {
                    Console.WriteLine(
                        "[EasyMulti] 只绑到了 localhost。要收本机以外的连接，用管理员跑一次："
                        + $"netsh http add urlacl url=http://+:{port}/ user=%USERNAME%");
                }

                return listener;
            }
            catch (HttpListenerException e)
            {
                Console.Error.WriteLine($"[EasyMulti] 绑定 {prefix} 失败（{e.Message}），换下一个");
                listener.Close();
            }
        }

        throw new InvalidOperationException($"端口 {port} 一个前缀都绑不上");
    }

    private static async Task AcceptLoop(HttpListener listener, ConcurrentQueue<RelayEvent> events)
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

            _ = HandleContext(context, events);
        }
    }

    private static async Task HandleContext(HttpListenerContext context, ConcurrentQueue<RelayEvent> events)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            // Not a WebSocket upgrade (e.g. a reverse-proxy health check). Answer 426 and move on.
            context.Response.StatusCode = (int)HttpStatusCode.UpgradeRequired;
            context.Response.Close();
            return;
        }

        HttpListenerWebSocketContext socketContext;
        try
        {
            socketContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[EasyMulti] WebSocket 握手失败：" + e.Message);
            context.Response.Abort();
            return;
        }

        string address = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
        var connection = new WebSocketConnection(socketContext.WebSocket, address, events);
        await connection.RunAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A single WebSocket client connection. Receive and send run on background pumps;
/// everything is handed to the relay core through the shared event queue, keeping the
/// core single-threaded and lock-free.
/// </summary>
public sealed class WebSocketConnection : IRelayConnection
{
    private const int ReceiveChunkBytes = 8 * 1024;

    private static int _nextId;

    private readonly WebSocket _socket;
    private readonly ConcurrentQueue<RelayEvent> _events;
    private readonly Channel<string> _outbox =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();
    private string? _closeReason;

    public WebSocketConnection(WebSocket socket, string address, ConcurrentQueue<RelayEvent> events)
    {
        _socket = socket;
        _events = events;
        Address = address;
        Id = $"ws-{Interlocked.Increment(ref _nextId)}";
    }

    public string Id { get; }
    public string Address { get; }
    public string TransportName => "WebSocket";

    public double UptimeSeconds => _uptime.Elapsed.TotalSeconds;

    public void Send(string json, DeliveryMode mode) => _outbox.Writer.TryWrite(json);

    public void Close(string reason)
    {
        _closeReason ??= reason;
        _outbox.Writer.TryComplete();
    }

    public async Task RunAsync()
    {
        _events.Enqueue(new RelayEvent(RelayEventKind.Connected, this, null, "", DeliveryMode.Reliable));
        Task send = PumpOutboxAsync();
        Task receive = PumpInboxAsync();
        await Task.WhenAny(send, receive).ConfigureAwait(false);
        _outbox.Writer.TryComplete();
        _socket.Dispose();
        _events.Enqueue(new RelayEvent(
            RelayEventKind.Disconnected, this, null,
            _closeReason ?? "连接已关闭", DeliveryMode.Reliable));
    }

    private async Task PumpOutboxAsync()
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out string? message))
                {
                    await _socket.SendAsync(
                            new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            CancellationToken.None)
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

                string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                _events.Enqueue(new RelayEvent(
                    RelayEventKind.Message, this, json, "", DeliveryMode.Reliable));
            }
        }
        catch (Exception e)
        {
            _closeReason ??= "接收失败：" + e.Message;
        }
    }
}
