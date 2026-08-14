#nullable enable

using EasyMulti.Protocol;
using EasyMulti.Relay.Transport;

namespace EasyMulti.Relay;

public enum RelayEventKind
{
    Connected,
    Message,
    Disconnected,
}

/// <summary>
/// An event handed from the transports' background I/O into the relay core's single
/// threaded main loop. Because everything funnels through this queue, the core's state
/// (<c>games</c> / <c>rooms</c> / <c>peers</c>) never needs a lock.
/// </summary>
public readonly record struct RelayEvent(
    RelayEventKind Kind,
    IRelayConnection Connection,
    string? Text,        // Kind == Message: the wire JSON text; otherwise null
    string Reason,       // Kind == Disconnected: human-readable cause; otherwise ""
    DeliveryMode Mode);  // Kind == Message: how it arrived (WebSocket is always Reliable)
