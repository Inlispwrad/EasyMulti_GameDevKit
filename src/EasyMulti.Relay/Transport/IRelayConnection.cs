#nullable enable

using EasyMultiNet.Protocol;

namespace EasyMultiNet.Relay.Transport;

/// <summary>
/// A transport-agnostic connection to one client, as seen by the relay core.
/// <para>
/// Messages are UTF-8 JSON strings (the relay protocol). The transport handles its own
/// framing — WebSocket text frames vs. EasyMulti UDP frames — and enqueues events into
/// the relay core's single-threaded queue, so the core itself never locks.
/// </para>
/// <para>
/// Reference equality is identity: the core uses the connection object as a dictionary key,
/// so implementers must not override Equals/GetHashCode.
/// </para>
/// </summary>
public interface IRelayConnection
{
    /// <summary>Stable unique id, e.g. "ws-1" or "udp-127.0.0.1:52341".</summary>
    string Id { get; }

    /// <summary>
    /// 这条连接的凭证 —— 它随连接请求一起到，并且在这个对象存在之前就已经验过了。
    /// 换句话说：**能拿到 IRelayConnection，就等于身份已经成立**，没有「连上了但还没注册」的中间态。
    /// </summary>
    RegisterRequest Credentials { get; }

    /// <summary>Human-readable peer address for logs.</summary>
    string Address { get; }

    /// <summary>Transport name for logs ("WebSocket" / "Udp").</summary>
    string TransportName { get; }

    /// <summary>Enqueue one message for delivery. Non-blocking; must preserve order per connection.</summary>
    /// <param name="json">The wire JSON text.</param>
    /// <param name="mode">
    /// Delivery hint. WebSocket always delivers reliably regardless; UDP honors it.
    /// </param>
    void Send(string json, DeliveryMode mode);

    /// <summary>Enqueue one game-data frame ([routing header + payload]; the relay never parses the payload).</summary>
    void SendBinary(byte[] frame, DeliveryMode mode);

    /// <summary>Close the connection. The disconnect event is reported through the event queue.</summary>
    void Close(string reason);
}
