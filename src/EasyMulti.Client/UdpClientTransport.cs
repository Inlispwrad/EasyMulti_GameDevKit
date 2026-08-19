#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyMultiNet.Protocol;

namespace EasyMultiNet
{
    /// <summary>
    /// UDP transport for the client. One socket, one <see cref="UdpPeer"/> to the relay.
    /// Control traffic rides the reliable channel; game state may use the best-effort channel.
    /// </summary>
    public sealed class UdpClientTransport : IClientTransport
    {
        /// <summary>
        /// 保活间隔。远小于 60 秒的 idle 超时，也小于常见 NAT 的映射超时
        /// （真实设备约 30 秒起；RFC 6263 给 UDP 定的下限是 15 秒）。
        /// </summary>
        private const long KeepAliveMs = 15_000;

        private enum EventKind { Received, Closed }

        private readonly ConcurrentQueue<(EventKind Kind, string Text, byte[]? Binary, DeliveryMode Mode)> _inbox =
            new ConcurrentQueue<(EventKind Kind, string Text, byte[]? Binary, DeliveryMode Mode)>();

        private Socket? _socket;
        private UdpPeer? _peer;
        private IPEndPoint? _remote;
        private volatile bool _disposed;
        private int _closedReported;

        public event Action? Opened;
        public event Action<string>? Closed;
        public event Action<string, DeliveryMode>? Received;
        public event Action<byte[], DeliveryMode>? ReceivedBinary;

        public void Connect(string host, int port, RegisterRequest credentials)
        {
            if (_socket != null) throw new InvalidOperationException("已经连过了");

            IPAddress ip = Resolve(host);
            _remote = new IPEndPoint(ip, port);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            // Bounded timeout so Dispose() can signal shutdown without closing the socket
            // from another thread (which can block on macOS while ReceiveFrom is pending).
            _socket.ReceiveTimeout = 500;

            _peer = new UdpPeer(
                _remote.ToString(),
                SendRaw,
                (payload, mode, isGameData) => _inbox.Enqueue(isGameData
                    ? (EventKind.Received, "", payload, mode)
                    : (EventKind.Received, Encoding.UTF8.GetString(payload), null, mode)),
                ReportClosed,
                // 客户端负责保活：它在 NAT 后面，而 NAT 映射只能被自己的出站包刷新，
                // 中继发什么都救不回一条已经过期的映射。中继那侧不设这个值，只回应。
                new UdpPeerOptions { KeepAliveMs = KeepAliveMs });

            _ = Task.Run(ReceiveLoop);

            // UDP 没有握手，HELLO 就是握手：凭证走可靠通道发过去，中继验过了才会为这条
            // 连接分配任何东西，验不过会回一个带理由的 Bye。
            _peer.SendHello(Encoding.UTF8.GetBytes(RelayCodec.Serialize(credentials)));
            Opened?.Invoke();
        }

        public void Poll()
        {
            _peer?.Tick();

            while (_inbox.TryDequeue(out (EventKind Kind, string Text, byte[]? Binary, DeliveryMode Mode) e))
            {
                if (e.Kind != EventKind.Received)
                {
                    Closed?.Invoke(e.Text);
                }
                else if (e.Binary != null)
                {
                    ReceivedBinary?.Invoke(e.Binary, e.Mode);
                }
                else
                {
                    Received?.Invoke(e.Text, e.Mode);
                }
            }
        }

        public void Send(string json, DeliveryMode mode)
        {
            if (_disposed || _peer == null) return;
            _peer.Send(Encoding.UTF8.GetBytes(json), mode);
        }

        public void SendBinary(byte[] frame, DeliveryMode mode)
        {
            if (_disposed || _peer == null) return;
            _peer.Send(frame, mode, gameData: true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            // 先 Close 再置 _disposed：Close 会给对端发 Bye 帧，SendRaw 此刻还得活着。
            _peer?.Close("client disconnect");
            _disposed = true;
            // The receive loop exits on its own within ReceiveTimeout and closes the socket.
        }

        private void SendRaw(byte[] data, int length)
        {
            if (_disposed) return;
            try
            {
                _socket!.SendTo(data, 0, length, SocketFlags.None, _remote!);
            }
            catch (SocketException)
            {
                // Best effort.
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[65535];
            try
            {
                while (!_disposed)
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int length;
                    try
                    {
                        length = _socket!.ReceiveFrom(buffer, ref remote);
                    }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
                    {
                        continue; // re-check _disposed
                    }
                    catch (SocketException)
                    {
                        if (_disposed) return;
                        continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    _peer?.HandleDatagram(buffer, length);
                }
            }
            finally
            {
                try { _socket?.Close(); } catch { /* already closed */ }
                _socket?.Dispose();
            }
        }

        private void ReportClosed(string reason)
        {
            if (Interlocked.Exchange(ref _closedReported, 1) != 0) return;
            _inbox.Enqueue((EventKind.Closed, reason, null, DeliveryMode.Reliable));
        }

        private static IPAddress Resolve(string host)
        {
            // Prefer IPv4; the relay's UDP transport binds IPv4 (0.0.0.0).
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.First();
        }
    }
}
