#nullable enable

using EasyMulti.Protocol;

namespace EasyMulti.Client;

/// <summary>
/// The client-side transport. <see cref="EasyMultiClient"/> depends only on this
/// interface, so swapping WebSocket for UDP (or a future Godot WebSocketPeer) never
/// touches the state machine.
/// <para>
/// <b>Contract:</b> all three events fire on the caller's thread, inside
/// <see cref="Poll"/>. The receive I/O may run on background tasks, but it must queue
/// and be drained by <see cref="Poll"/>, so the client stays single-threaded and lock-free.
/// </para>
/// </summary>
public interface IClientTransport : IDisposable
{
    /// <summary>Connection established. The client replies with REGISTER.</summary>
    event Action? Opened;

    /// <summary>Connection failed or dropped; the argument is a human-readable reason.</summary>
    event Action<string>? Closed;

    /// <summary>A complete message arrived (wire JSON + how it was delivered).</summary>
    event Action<string, DeliveryMode>? Received;

    /// <summary>Begin connecting asynchronously; success/failure is reported via Opened/Closed.</summary>
    void Connect(string host, int port);

    /// <summary>Drain queued events and drive background bookkeeping. Call every 10–20 ms.</summary>
    void Poll();

    /// <summary>Send one message. Order-preserving and non-blocking.</summary>
    void Send(string json, DeliveryMode mode);
}
