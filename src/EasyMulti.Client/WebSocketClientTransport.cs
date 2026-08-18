#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyMultiNet.Protocol;

namespace EasyMultiNet
{
    /// <summary>
    /// WebSocket transport built on BCL <see cref="ClientWebSocket"/>. Zero third-party deps.
    /// WebSocket is TCP-backed, so every message is delivered reliably+ordered regardless of
    /// the requested <see cref="DeliveryMode"/>.
    /// <para>
    /// 明文 <c>ws://</c> 与 TLS <c>wss://</c> 都支持，见构造函数的 secure 参数。
    /// </para>
    /// </summary>
    public sealed class WebSocketClientTransport : IClientTransport
    {
        private const int ReceiveChunkBytes = 8 * 1024;
        private const int CloseGraceMillis = 250;

        private enum EventKind { Opened, Received, Closed }

        private readonly ConcurrentQueue<(EventKind Kind, string Payload, byte[]? Binary)> _inbox =
            new ConcurrentQueue<(EventKind Kind, string Payload, byte[]? Binary)>();

        // System.Threading.Channels is not part of netstandard2.1 (and is not shipped with
        // Unity), so the single-reader outbox is a plain queue plus a signal. The semaphore
        // is intentionally never disposed: it allocates no wait handle unless
        // AvailableWaitHandle is touched, and skipping disposal avoids a race with Send.
        private readonly ConcurrentQueue<(string? Text, byte[]? Binary)> _outbox = new ConcurrentQueue<(string? Text, byte[]? Binary)>();
        private readonly SemaphoreSlim _outboxSignal = new SemaphoreSlim(0);
        private volatile bool _outboxClosed;

        private readonly bool _secure;
        private readonly string _path;

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cancel;
        private int _closedReported;

        public event Action? Opened;
        public event Action<string>? Closed;
        public event Action<string, DeliveryMode>? Received;
        public event Action<byte[], DeliveryMode>? ReceivedBinary;

        /// <param name="secure">
        /// true 走 <c>wss://</c>。浏览器 / WASM 里的 HTTPS 页面只能连 wss —— 明文 ws 会被
        /// 混合内容策略拦掉。中继自己只跑明文，TLS 由它前面的反向代理终结（见 docs/DEPLOY.md）。
        /// </param>
        /// <param name="path">反代把中继挂在哪个路径下，比如 <c>"/em"</c>。默认根路径。</param>
        public WebSocketClientTransport(bool secure = false, string path = "/")
        {
            _secure = secure;
            _path = path;
        }

        /// <summary>
        /// 中继地址的拼装规则。单独暴露，是因为真 wss 的端到端在自动化测试里跑不起来
        /// （要真证书；netstandard2.1 的 ClientWebSocketOptions 没有跳过证书校验的口子），
        /// 至少把「拼对地址」这半守住。
        /// </summary>
        public static Uri BuildUrl(string host, int port, bool secure, string path)
        {
            string scheme = secure ? "wss" : "ws";
            string tail = string.IsNullOrEmpty(path) ? "/" : (path[0] == '/' ? path : "/" + path);
            return new Uri($"{scheme}://{host}:{port}{tail}");
        }

        public void Connect(string host, int port, RegisterRequest credentials)
        {
            if (_socket != null) throw new InvalidOperationException("已经连过了");
            _socket = new ClientWebSocket();
            // 凭证走子协议名而不是自定义头 —— 浏览器 / WASM 里的 WebSocket 设不了头，
            // 而且这样 token 不会落进反代的 access log。服务端验完才会升级。
            _socket.Options.AddSubProtocol(RelayHandshake.Protocol);
            _socket.Options.AddSubProtocol(RelayHandshake.Encode(credentials));
            _cancel = new CancellationTokenSource();
            _ = RunAsync(BuildUrl(host, port, _secure, _path), _socket, _cancel.Token);
        }

        public void Poll()
        {
            while (_inbox.TryDequeue(out (EventKind Kind, string Payload, byte[]? Binary) e))
            {
                switch (e.Kind)
                {
                    case EventKind.Opened: Opened?.Invoke(); break;
                    case EventKind.Received when e.Binary != null: ReceivedBinary?.Invoke(e.Binary, DeliveryMode.Reliable); break;
                    case EventKind.Received: Received?.Invoke(e.Payload, DeliveryMode.Reliable); break;
                    case EventKind.Closed: Closed?.Invoke(e.Payload); break;
                }
            }
        }

        public void Send(string json, DeliveryMode mode)
        {
            if (_outboxClosed) return;
            _outbox.Enqueue((json, null));
            _outboxSignal.Release();
        }

        public void SendBinary(byte[] frame, DeliveryMode mode)
        {
            if (_outboxClosed) return;
            _outbox.Enqueue((null, frame));
            _outboxSignal.Release();
        }

        public void Dispose()
        {
            CompleteOutbox();
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

        /// <summary>Mark the outbox finished so the send pump can drain and exit.</summary>
        private void CompleteOutbox()
        {
            if (_outboxClosed) return;
            _outboxClosed = true;
            _outboxSignal.Release(); // wake the pump so it observes the close
        }

        /// <summary>
        /// Block until at least one message is queued. Returns false once the outbox is
        /// closed and drained. Surplus signals (more releases than waits) are absorbed by
        /// the loop, one per iteration, so this never spins unbounded.
        /// </summary>
        private async Task<bool> WaitForOutboxAsync(CancellationToken token)
        {
            while (true)
            {
                if (!_outbox.IsEmpty) return true;
                if (_outboxClosed) return false;
                await _outboxSignal.WaitAsync(token).ConfigureAwait(false);
            }
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

            _inbox.Enqueue((EventKind.Opened, "", null));

            Task send = PumpOutboxAsync(socket, token);
            Task receive = PumpInboxAsync(socket, token);
            await Task.WhenAny(send, receive).ConfigureAwait(false);

            ReportClosed("与中继的连接已断开");
            CompleteOutbox();
        }

        private async Task PumpOutboxAsync(ClientWebSocket socket, CancellationToken token)
        {
            try
            {
                while (await WaitForOutboxAsync(token).ConfigureAwait(false))
                {
                    while (_outbox.TryDequeue(out (string? Text, byte[]? Binary) message))
                    {
                        byte[] bytes = message.Binary ?? Encoding.UTF8.GetBytes(message.Text!);
                        WebSocketMessageType kind = message.Binary != null
                            ? WebSocketMessageType.Binary
                            : WebSocketMessageType.Text;
                        await socket.SendAsync(new ArraySegment<byte>(bytes), kind, endOfMessage: true, token)
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
            using (var message = new MemoryStream())
            {
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

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            _inbox.Enqueue((EventKind.Received, "", message.ToArray()));
                        }
                        else
                        {
                            string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                            _inbox.Enqueue((EventKind.Received, json, null));
                        }

                        message.SetLength(0);
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
        }

        private void ReportClosed(string reason)
        {
            if (Interlocked.Exchange(ref _closedReported, 1) != 0) return;
            _inbox.Enqueue((EventKind.Closed, reason, null));
        }
    }
}
