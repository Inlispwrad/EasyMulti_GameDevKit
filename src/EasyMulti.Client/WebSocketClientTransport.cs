#nullable enable

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using EasyMulti.Protocol;

namespace EasyMulti.Client;

/// <summary>
/// WebSocket transport built on BCL <see cref="ClientWebSocket"/>. Zero third-party deps.
/// WebSocket is TCP-backed, so every message is delivered reliably+ordered regardless of
/// the requested <see cref="DeliveryMode"/>.
/// </summary>
public sealed class WebSocketClientTransport : IClientTransport
{
    private const int ReceiveChunkBytes = 8 * 1024;
    private const int CloseGraceMillis = 250;

    private enum EventKind { Opened, Received, Closed }

    private readonly ConcurrentQueue<(EventKind Kind, string Payload)> _inbox = new();
    private readonly Channel<string> _outbox =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancel;
    private int _closedReported;

    public event Action? Opened;
    public event Action<string>? Closed;
    public event Action<string, DeliveryMode>? Received;

    public void Connect(string host, int port)
    {
        if (_socket != null) throw new InvalidOperationException("已经连过了");
        _socket = new ClientWebSocket();
        _cancel = new CancellationTokenSource();
        _ = RunAsync(new Uri($"ws://{host}:{port}/"), _socket, _cancel.Token);
    }

    public void Poll()
    {
        while (_inbox.TryDequeue(out (EventKind Kind, string Payload) e))
        {
            switch (e.Kind)
            {
                case EventKind.Opened: Opened?.Invoke(); break;
                case EventKind.Received: Received?.Invoke(e.Payload, DeliveryMode.Reliable); break;
                case EventKind.Closed: Closed?.Invoke(e.Payload); break;
            }
        }
    }

    public void Send(string json, DeliveryMode mode) => _outbox.Writer.TryWrite(json);

    public void Dispose()
    {
        _outbox.Writer.TryComplete();
        ClientWebSocket? socket = _socket;
        CancellationTokenSource? cancel = _cancel;
        _socket = null;
        _cancel = null;
        _ = TearDownAsync(socket, cancel);
    }

    private static async Task TearDownAsync(ClientWebSocket? socket, CancellationTokenSource? cancel)
    {
        await Task.Delay(CloseGraceMillis).ConfigureAwait(false);
        cancel?.Cancel();
        socket?.Dispose();
        cancel?.Dispose();
    }

    private async Task RunAsync(Uri url, ClientWebSocket socket, CancellationToken token)
    {
        try
        {
            await socket.ConnectAsync(url, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            ReportClosed($"连不上中继 {url}：{e.Message}");
            return;
        }

        _inbox.Enqueue((EventKind.Opened, ""));

        Task send = PumpOutboxAsync(socket, token);
        Task receive = PumpInboxAsync(socket, token);
        await Task.WhenAny(send, receive).ConfigureAwait(false);

        ReportClosed("与中继的连接已断开");
        _outbox.Writer.TryComplete();
    }

    private async Task PumpOutboxAsync(ClientWebSocket socket, CancellationToken token)
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out string? message))
                {
                    await socket.SendAsync(
                            new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            token)
                        .ConfigureAwait(false);
                }
            }

            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client disconnect", token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispose path.
        }
        catch (Exception e)
        {
            ReportClosed("发送失败：" + e.Message);
        }
    }

    private async Task PumpInboxAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] chunk = new byte[ReceiveChunkBytes];
        using var message = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ReportClosed("中继关闭了连接：" + (result.CloseStatusDescription ?? "无理由"));
                        return;
                    }

                    message.Write(chunk, 0, result.Count);
                }
                while (!result.EndOfMessage);

                string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                _inbox.Enqueue((EventKind.Received, json));
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose path.
        }
        catch (Exception e)
        {
            ReportClosed("接收失败：" + e.Message);
        }
    }

    private void ReportClosed(string reason)
    {
        if (Interlocked.Exchange(ref _closedReported, 1) != 0) return;
        _inbox.Enqueue((EventKind.Closed, reason));
    }
}
