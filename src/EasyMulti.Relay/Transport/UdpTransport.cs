#nullable enable

using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyMultiNet.Protocol;
using EasyMultiNet.Relay;

namespace EasyMultiNet.Relay.Transport;

/// <summary>
/// UDP transport. One bound socket, one <see cref="UdpConnection"/> per client endpoint
/// (NAT keeps the source port stable for the session). Each connection owns a
/// <see cref="UdpPeer"/> that provides reliable in-order delivery for control traffic and
/// best-effort datagrams for game state.
/// <para>
/// UDP 没有握手，所以协议自己造了一个：陌生地址发来的**第一个**数据报必须是带凭证的
/// HELLO 帧（<c>FrameFlags.Hello</c>）。不是 HELLO、解不开、或凭证验不过 —— 一律丢弃，
/// **不创建 UdpConnection、不创建 UdpPeer、不入表、不发 Connected 事件**。所以匿名来源
/// 无法让中继为它保留任何东西。
/// </para>
/// </summary>
public sealed class UdpTransport : IRelayTransport
{
    private readonly int _port;
    private readonly UdpPeerOptions _peerOptions;
    private Socket? _socket;
    private Action<RelayEvent>? _enqueue;
    private Func<RegisterRequest, string, string?>? _authenticate;
    private readonly Dictionary<IPEndPoint, UdpConnection> _connections = new();
    private readonly object _gate = new();
    private volatile bool _stopped;

    public UdpTransport(int port, UdpPeerOptions? peerOptions = null)
    {
        _port = port;
        _peerOptions = peerOptions ?? new UdpPeerOptions();
    }

    public void Start(Action<RelayEvent> enqueue, Func<RegisterRequest, string, string?> authenticate)
    {
        _enqueue = enqueue;
        _authenticate = authenticate;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
        // A bounded receive timeout lets Stop() signal shutdown without calling
        // Socket.Close() from another thread, which can block on macOS while
        // ReceiveFrom is pending.
        _socket.ReceiveTimeout = 500;
        Console.WriteLine($"[EasyMulti] UDP listening on udp://0.0.0.0:{_port}");
        _ = Task.Run(ReceiveLoop);
    }

    public void Stop()
    {
        _stopped = true;

        List<UdpConnection> connections;
        lock (_gate)
        {
            connections = _connections.Values.ToList();
            _connections.Clear();
        }

        foreach (UdpConnection c in connections)
        {
            c.Close("server shutdown");
        }

        // The receive loop exits on its own within ReceiveTimeout and closes the socket.
    }

    public void Tick()
    {
        List<UdpConnection>? toDrop = null;

        lock (_gate)
        {
            foreach (UdpConnection c in _connections.Values)
            {
                c.Peer.Tick();
                if (c.IsClosed)
                {
                    (toDrop ??= new List<UdpConnection>()).Add(c);
                }
            }

            if (toDrop != null)
            {
                foreach (UdpConnection c in toDrop)
                {
                    _connections.Remove(c.Endpoint);
                }
            }
        }
    }

    public void Dispose() => Stop();

    private void ReceiveLoop()
    {
        var buffer = new byte[65535];
        try
        {
            while (!_stopped)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int length;
                try
                {
                    length = _socket!.ReceiveFrom(buffer, ref remote);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
                {
                    continue; // re-check _stopped
                }
                catch (SocketException)
                {
                    if (_stopped) return;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                UdpConnection? connection = Accept((IPEndPoint)remote, buffer, length);
                connection?.Peer.HandleDatagram(buffer, length);
            }
        }
        finally
        {
            try { _socket?.Close(); } catch { /* already closed */ }
            _socket?.Dispose();
        }
    }

    /// <summary>
    /// 已知地址直接返回它的连接；陌生地址必须先用一个合格的 HELLO 把自己证明了才会被建出来。
    /// 返回 null＝这个数据报到此为止，没有任何状态被创建。
    /// </summary>
    private UdpConnection? Accept(IPEndPoint endpoint, byte[] data, int length)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(endpoint, out UdpConnection? existing))
            {
                // 已经关掉、还没被 Tick 清走的旧连接不能挡住同一个地址的重连
                // （NAT 后重连很容易拿到同一个源端口）。就地摘掉，让它重新走门口。
                if (!existing.IsClosed) return existing;
                _connections.Remove(endpoint);
            }
        }

        if (!TryReadHello(data, length, out RegisterRequest credentials))
        {
            return null; // 不是连接请求：陌生地址没资格让我们记住它
        }

        if (_authenticate!(credentials, endpoint.ToString()) is string refusal)
        {
            // 回一句为什么（Bye + reason），但仍然什么都不建。回包比来包小，没有放大价值。
            Refuse(endpoint, refusal);
            return null;
        }

        lock (_gate)
        {
            // 双检：同一个地址的两个 HELLO 可能挨着进来（HELLO 会重传）。
            if (_connections.TryGetValue(endpoint, out UdpConnection? raced)) return raced;

            var connection = new UdpConnection(endpoint, _socket!, credentials, _enqueue!, _peerOptions);
            _connections[endpoint] = connection;
            // Connected 事件先于这条 HELLO 产生的任何后续消息入队（都来自这个线程，顺序有保证）。
            _enqueue!(new RelayEvent(RelayEventKind.Connected, connection, null, null, "", DeliveryMode.Reliable));
            return connection;
        }
    }

    /// <summary>这个数据报是不是一个自带凭证的连接请求。凭证必须一个包装得下（不接受分片）。</summary>
    private static bool TryReadHello(byte[] data, int length, out RegisterRequest credentials)
    {
        credentials = default!;
        if (!UdpFrame.TryRead(data.AsSpan(0, length), out FrameHeader header, out ReadOnlySpan<byte> payload))
        {
            return false;
        }

        if ((header.Flags & FrameFlags.Hello) == 0 || header.FragCount > 1) return false;
        return RelayCodec.TryDeserialize(Encoding.UTF8.GetString(payload.ToArray()), out credentials);
    }

    /// <summary>告诉对方为什么进不来。无状态：一个 Bye 帧带上理由，发完就忘。</summary>
    private void Refuse(IPEndPoint endpoint, string reason)
    {
        byte[] why = Encoding.UTF8.GetBytes(reason);
        var buffer = new byte[UdpFrame.HeaderSize + why.Length];
        int written = UdpFrame.Write(buffer, new FrameHeader(FrameFlags.Bye, 0, 0, 0, 0, 0), why);
        try { _socket!.SendTo(buffer, 0, written, SocketFlags.None, endpoint); }
        catch (SocketException) { /* best effort */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }
}

/// <summary>
/// One UDP client endpoint. Wraps a <see cref="UdpPeer"/> and bridges its callbacks into
/// the relay core's event queue. Reference equality is identity (used as a dictionary key
/// by the core), so Equals is not overridden.
/// </summary>
public sealed class UdpConnection : IRelayConnection
{
    private readonly Socket _socket;
    private readonly Action<RelayEvent> _enqueue;
    private volatile bool _closed;

    public UdpConnection(
        IPEndPoint endpoint, Socket socket, RegisterRequest credentials,
        Action<RelayEvent> enqueue, UdpPeerOptions options)
    {
        Endpoint = endpoint;
        _socket = socket;
        _enqueue = enqueue;
        Credentials = credentials;
        Address = endpoint.ToString();
        Id = $"udp-{endpoint}";
        Peer = new UdpPeer(
            endpoint.ToString(),
            SendRaw,
            Deliver,
            OnPeerClosed,
            options);
    }

    public IPEndPoint Endpoint { get; }
    public UdpPeer Peer { get; }
    public string Id { get; }
    public RegisterRequest Credentials { get; }
    public string Address { get; }
    public string TransportName => "Udp";
    public bool IsClosed => _closed;

    public void Send(string json, DeliveryMode mode)
    {
        if (_closed) return;
        Peer.Send(Encoding.UTF8.GetBytes(json), mode);
    }

    public void SendBinary(byte[] frame, DeliveryMode mode)
    {
        if (_closed) return;
        Peer.Send(frame, mode, gameData: true);
    }

    public void Close(string reason)
    {
        Peer.Close(reason);
    }

    private void SendRaw(byte[] data, int length)
    {
        if (_closed) return;
        try
        {
            _socket.SendTo(data, 0, length, SocketFlags.None, Endpoint);
        }
        catch (SocketException)
        {
            // Best effort. A dead endpoint is detected by the idle timeout.
        }
        catch (ObjectDisposedException)
        {
            // Socket shut down.
        }
    }

    private void Deliver(byte[] payload, DeliveryMode mode, bool isGameData)
    {
        if (_closed) return;
        _enqueue(isGameData
            ? new RelayEvent(RelayEventKind.Message, this, null, payload, "", mode)
            : new RelayEvent(RelayEventKind.Message, this, Encoding.UTF8.GetString(payload), null, "", mode));
    }

    private void OnPeerClosed(string reason)
    {
        if (_closed) return;
        _closed = true;
        _enqueue(new RelayEvent(
            RelayEventKind.Disconnected, this, null, null, reason, DeliveryMode.Reliable));
    }
}
