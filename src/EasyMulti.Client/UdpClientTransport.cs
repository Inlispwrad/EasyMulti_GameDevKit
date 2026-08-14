#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyMulti.Protocol;

namespace EasyMulti.Client;

/// <summary>
/// UDP transport for the client. One socket, one <see cref="UdpPeer"/> to the relay.
/// Control traffic rides the reliable channel; game state may use the best-effort channel.
/// </summary>
public sealed class UdpClientTransport : IClientTransport
{
    private enum EventKind { Received, Closed }

    private readonly ConcurrentQueue<(EventKind Kind, string Text, DeliveryMode Mode)> _inbox = new();
    private Socket? _socket;
    private UdpPeer? _peer;
    private IPEndPoint? _remote;
    private volatile bool _disposed;
    private int _closedReported;

    public event Action? Opened;
    public event Action<string>? Closed;
    public event Action<string, DeliveryMode>? Received;

    public void Connect(string host, int port)
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
            (payload, mode) => _inbox.Enqueue((EventKind.Received, Encoding.UTF8.GetString(payload), mode)),
            ReportClosed);

        _ = Task.Run(ReceiveLoop);
        Opened?.Invoke();
    }

    public void Poll()
    {
        _peer?.Tick();

        while (_inbox.TryDequeue(out (EventKind Kind, string Text, DeliveryMode Mode) e))
        {
            if (e.Kind == EventKind.Received)
            {
                Received?.Invoke(e.Text, e.Mode);
            }
            else
            {
                Closed?.Invoke(e.Text);
            }
        }
    }

    public void Send(string json, DeliveryMode mode)
    {
        if (_disposed || _peer == null) return;
        _peer.Send(Encoding.UTF8.GetBytes(json), mode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _peer?.Close("client disconnect");
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
        _inbox.Enqueue((EventKind.Closed, reason, DeliveryMode.Reliable));
    }

    private static IPAddress Resolve(string host)
    {
        // Prefer IPv4; the relay's UDP transport binds IPv4 (0.0.0.0).
        IPAddress[] addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.First();
    }
}
