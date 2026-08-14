#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyMulti.Protocol;
using EasyMulti.Relay;

namespace EasyMulti.Relay.Transport;

/// <summary>
/// UDP transport. One bound socket, one <see cref="UdpConnection"/> per client endpoint
/// (adopted lazily on the first datagram — NAT keeps the source port stable for the
/// session). Each connection owns a <see cref="UdpPeer"/> that provides reliable in-order
/// delivery for control traffic and best-effort datagrams for game state.
/// </summary>
public sealed class UdpTransport : IRelayTransport
{
    private readonly int _port;
    private readonly UdpPeerOptions _peerOptions;
    private Socket? _socket;
    private ConcurrentQueue<RelayEvent>? _events;
    private readonly Dictionary<IPEndPoint, UdpConnection> _connections = new();
    private readonly object _gate = new();
    private volatile bool _stopped;

    public UdpTransport(int port, UdpPeerOptions? peerOptions = null)
    {
        _port = port;
        _peerOptions = peerOptions ?? new UdpPeerOptions();
    }

    public void Start(ConcurrentQueue<RelayEvent> events)
    {
        _events = events;
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

                UdpConnection connection = GetOrCreate((IPEndPoint)remote);
                connection.Peer.HandleDatagram(buffer, length);
            }
        }
        finally
        {
            try { _socket?.Close(); } catch { /* already closed */ }
            _socket?.Dispose();
        }
    }

    private UdpConnection GetOrCreate(IPEndPoint endpoint)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(endpoint, out UdpConnection? existing))
            {
                return existing;
            }

            var connection = new UdpConnection(endpoint, _socket!, _events!, _peerOptions);
            _connections[endpoint] = connection;
            // Adopt the endpoint as a connection before its first message is processed,
            // so the relay core registers it (the Connected event precedes the Message
            // event because both are enqueued sequentially from this thread).
            _events!.Enqueue(new RelayEvent(RelayEventKind.Connected, connection, null, "", DeliveryMode.Reliable));
            return connection;
        }
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
    private readonly ConcurrentQueue<RelayEvent> _events;
    private volatile bool _closed;

    public UdpConnection(IPEndPoint endpoint, Socket socket, ConcurrentQueue<RelayEvent> events, UdpPeerOptions options)
    {
        Endpoint = endpoint;
        _socket = socket;
        _events = events;
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
    public string Address { get; }
    public string TransportName => "Udp";
    public bool IsClosed => _closed;

    public void Send(string json, DeliveryMode mode)
    {
        if (_closed) return;
        Peer.Send(Encoding.UTF8.GetBytes(json), mode);
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

    private void Deliver(byte[] payload, DeliveryMode mode)
    {
        if (_closed) return;
        _events.Enqueue(new RelayEvent(
            RelayEventKind.Message, this, Encoding.UTF8.GetString(payload), "", mode));
    }

    private void OnPeerClosed(string reason)
    {
        if (_closed) return;
        _closed = true;
        _events.Enqueue(new RelayEvent(
            RelayEventKind.Disconnected, this, null, reason, DeliveryMode.Reliable));
    }
}
